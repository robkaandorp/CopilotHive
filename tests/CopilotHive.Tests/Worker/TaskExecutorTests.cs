using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// The shared PREPARATION answers for the Improver's pre-run baseline restore, reused by every
/// <see cref="TaskExecutor"/> fake in this test project.
/// <para>
/// Every answer is an EXPLICIT valid value — a real worktree root, a full 40-hex commit SHA, an
/// attached <c>refs/heads/…</c> branch, a matching <c>refs/remotes/origin/…</c> upstream and an
/// empty verbose status. A bare exit-zero/empty-output response must NEVER become a usable
/// baseline, so no fixture may rely on the fallthrough success of an unanswered command.
/// </para>
/// </summary>
internal static class ConfigRepoPreparationFakes
{
    /// <summary>The full 40-hex SHA the fakes report for HEAD and for the fetched baseline.</summary>
    internal const string BaselineSha = "1111111111111111111111111111111111111111";

    /// <summary>The attached branch the fakes report from <c>--symbolic-full-name HEAD</c>.</summary>
    internal const string Branch = "main";

    /// <summary>The full ref of <see cref="Branch"/>.</summary>
    internal const string BranchRef = "refs/heads/" + Branch;

    /// <summary>The upstream the fakes report from <c>--symbolic-full-name @{upstream}</c>.</summary>
    internal const string UpstreamRef = "refs/remotes/origin/" + Branch;

    /// <summary>The canonical spelling the production code compares the worktree root against.</summary>
    internal static string CanonicalRoot(string configRepoDir) =>
        Path.GetFullPath(configRepoDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// The LEGACY opaque-argument answers. Returns <c>null</c> for every non-preparation
    /// command so the caller can fall through to its own scripted behavior.
    /// </summary>
    internal static (int ExitCode, string Stdout, string Stderr)? LegacyAnswer(
        string workDir, string args) => args switch
    {
        "rev-parse --show-toplevel" => (0, CanonicalRoot(workDir) + "\n", ""),
        "rev-parse --verify HEAD^{commit}" => (0, BaselineSha + "\n", ""),
        "rev-parse --verify FETCH_HEAD^{commit}" => (0, BaselineSha + "\n", ""),
        "rev-parse --symbolic-full-name HEAD" => (0, BranchRef + "\n", ""),
        "rev-parse --symbolic-full-name @{upstream}" => (0, UpstreamRef + "\n", ""),
        "status --porcelain=v1 --untracked-files=all --ignored" => (0, "", ""),
        _ => null,
    };

    /// <summary>
    /// The TOKENIZED answers for the injected seam path. Returns <c>null</c> for every
    /// non-preparation command.
    /// </summary>
    internal static GitProcessResult? SeamAnswer(
        string workingDirectory, IReadOnlyList<string> tokens) => tokens switch
    {
        ["rev-parse", "--show-toplevel"] =>
            new GitProcessResult(0, CanonicalRoot(workingDirectory) + "\n", ""),
        ["rev-parse", "--verify", "HEAD^{commit}"] => new GitProcessResult(0, BaselineSha + "\n", ""),
        ["rev-parse", "--verify", "FETCH_HEAD^{commit}"] => new GitProcessResult(0, BaselineSha + "\n", ""),
        ["rev-parse", "--symbolic-full-name", "HEAD"] => new GitProcessResult(0, BranchRef + "\n", ""),
        ["rev-parse", "--symbolic-full-name", "@{upstream}"] => new GitProcessResult(0, UpstreamRef + "\n", ""),
        ["status", "--porcelain=v1", "--untracked-files=all", "--ignored"] =>
            new GitProcessResult(0, "", ""),
        _ => null,
    };

    /// <summary>The LEGACY opaque strings the preparation issues, in order.</summary>
    internal static string[] LegacyCommands =>
    [
        "rev-parse --show-toplevel",
        "rev-parse --verify HEAD^{commit}",
        "rev-parse --symbolic-full-name HEAD",
        "rev-parse --symbolic-full-name @{upstream}",
        $"fetch origin \"{BranchRef}\"",
        "rev-parse --verify FETCH_HEAD^{commit}",
        $"reset --hard {BaselineSha}",
        "clean -fdx",
        "rev-parse --verify HEAD^{commit}",
        "status --porcelain=v1 --untracked-files=all --ignored",
    ];

    /// <summary>
    /// The LEGACY opaque strings the step-end CLEANUP (finalization) issues, in order. The
    /// fakes answer every HEAD/FETCH_HEAD probe with <see cref="BaselineSha"/>, so a
    /// confirmed publication resolves the SAME SHA and the cleanup's reset target is
    /// <see cref="BaselineSha"/> in every fake-driven flow.
    /// </summary>
    internal static string[] LegacyCleanupCommands =>
    [
        "rev-parse --show-toplevel",
        "rev-parse --symbolic-full-name HEAD",
        "rev-parse --symbolic-full-name @{upstream}",
        $"reset --hard {BaselineSha}",
        "clean -fdx",
        "rev-parse --verify HEAD^{commit}",
        "status --porcelain=v1 --untracked-files=all --ignored",
    ];

    /// <summary>
    /// The TOKENIZED commands the seam launches for the step-end CLEANUP, in order — including
    /// the trust revalidation's own <c>check-ref-format</c> for the revalidated branch.
    /// </summary>
    internal static string[][] SeamCleanupLaunches =>
    [
        ["rev-parse", "--show-toplevel"],
        ["rev-parse", "--symbolic-full-name", "HEAD"],
        ["check-ref-format", "--allow-onelevel", BranchRef],
        ["rev-parse", "--symbolic-full-name", "@{upstream}"],
        ["reset", "--hard", BaselineSha],
        ["clean", "-fdx"],
        ["rev-parse", "--verify", "HEAD^{commit}"],
        ["status", "--porcelain=v1", "--untracked-files=all", "--ignored"],
    ];

    /// <summary>
    /// The TOKENIZED commands the seam launches for the preparation, in order — including the
    /// PREFLIGHT's own <c>check-ref-format</c> (the complete ref validation run once the branch
    /// is discovered, BEFORE either route builds a fetch form) and the seam's own
    /// <c>check-ref-format</c> plus origin-inspection preamble for the fetch itself.
    /// </summary>
    internal static string[][] SeamLaunches => SeamLaunchesWithOriginRepair(null);

    /// <summary>
    /// <see cref="SeamLaunches"/> with an optional Stage 6d origin REPAIR command inserted
    /// between the origin inspection and the fetch.
    /// </summary>
    internal static string[][] SeamLaunchesWithOriginRepair(string[]? repair) =>
    [
        ["rev-parse", "--show-toplevel"],
        ["rev-parse", "--verify", "HEAD^{commit}"],
        ["rev-parse", "--symbolic-full-name", "HEAD"],
        // The PREFLIGHT ref validation: the discovered ref gets git's authoritative verdict
        // BEFORE the upstream check and before any fetch form exists, so the tokenized and
        // legacy routes can never disagree about whether the ref is usable.
        ["check-ref-format", "--allow-onelevel", BranchRef],
        ["rev-parse", "--symbolic-full-name", "@{upstream}"],
        // The seam's own in-transport ref validation for the fetch it is about to launch.
        ["check-ref-format", "--allow-onelevel", BranchRef],
        ["remote", "get-url", "origin"],
        .. repair is null ? Array.Empty<string[]>() : [repair],
        ["fetch", "origin", BranchRef],
        ["rev-parse", "--verify", "FETCH_HEAD^{commit}"],
        ["reset", "--hard", BaselineSha],
        ["clean", "-fdx"],
        ["rev-parse", "--verify", "HEAD^{commit}"],
        ["status", "--porcelain=v1", "--untracked-files=all", "--ignored"],
    ];
}

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
            // A test's own script always wins; otherwise the Improver preparation commands get
            // EXPLICIT valid answers (root/SHA/branch/upstream/empty status). Exit-zero with an
            // empty stdout is never a usable baseline, so the fallthrough must not supply one.
            return Task.FromResult(
                scripted
                ?? ConfigRepoPreparationFakes.LegacyAnswer(workDir, args)
                ?? (0, "", ""));
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

        /// <summary>Every prompt and working directory delivered to this runner, in order.</summary>
        public List<(string Prompt, string WorkDir)> PromptCalls { get; } = [];

        /// <summary>
        /// Optional response hook used by tests that need the fake agent to perform a prompted
        /// file repair before <see cref="TaskExecutor"/> re-checks the agents.md size.
        /// </summary>
        public Func<string, string, CancellationToken, Task<string>>? PromptResponder { get; set; }

        public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) => CapturedSubAgentModels = models;
        public void SetSession(object? session) => _session = session;
        public object? GetSession() => _session;
        public int GetContextUsagePercent() => 0;

        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) { }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default) => Task.CompletedTask;
        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            // After TaskExecutor clears reports (ClearWorkerReport/ClearTestReport), inject the
            // mock reports here so they are visible to the code that reads LastWorkerReport/LastTestReport.
            LastWorkerReport = WorkerReportToReturn;
            LastTestReport = TestReportToReturn;
            LastWorkDir = workDir;
            LastPrompt = prompt;
            PromptCalls.Add((prompt, workDir));
            return PromptResponder is null
                ? "Mock agent response"
                : await PromptResponder(prompt, workDir, ct);
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

    private static WorkTask BuildImproverTask(string id, string model = "") => new()
    {
        TaskId = id,
        GoalId = $"goal-{id}",
        GoalDescription = "Improve the agents",
        Prompt = "Improve prompt",
        Role = WorkerRole.Improver,
        Model = model,
        Repositories = [Repo("test-repo")],
    };

    /// <summary>
    /// The actual overflow retry delivered through <see cref="IAgentRunner"/> must carry the
    /// append-new/compress-old policy, the concrete violation, and the configured limit. The
    /// retry must also explicitly forbid meeting the limit by truncating a whole file and must
    /// require the compressed result to stay readable to a new reader (useful headings, concise
    /// ordinary-language bullets). The fake repairs the real oversized file on that retry by
    /// compressing the OLD material from the top while keeping the heading and the newly
    /// appended lesson intact — the behaviour the prompt asks for — so this exercises successful
    /// enforcement rather than the exhausted-retry rollback path.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ImproverOverflow_DeliversTopFirstRepairPromptAndAcceptsRepair()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fileName = "capacity-policy.agents.md";
        var filePath = Path.Combine(configRepoDir, "agents", fileName);

        // A realistically structured file: a heading, a body of older bullets, and a newly
        // appended lesson at the very end. Repairing it by cutting the file short or replacing
        // it with a stub is precisely what the delivered retry prompt must forbid.
        const string heading = "# Older guidance\n";
        const string appendedSection =
            "\n## Newly appended lessons\n- Check guidance file sizes before and after editing.\n";
        const string oldBullet = "- Older rule: release every resource on every code path.\n";
        var olderBody = string.Concat(Enumerable.Repeat(oldBullet, 100));
        var oversized = heading + olderBody
            + new string('x', WorkerConstants.AgentsMdMaxCharacters + 1
                - heading.Length - olderBody.Length - appendedSection.Length)
            + appendedSection;

        var observedLength = oversized.Length;
        Assert.Equal(8_001, observedLength);
        Assert.Equal(WorkerConstants.AgentsMdMaxCharacters + 1, observedLength);

        // The compliant repair: older material consolidated from the top, heading kept, and the
        // appended lesson preserved byte-for-byte. Nothing is truncated away wholesale.
        var repaired = heading
            + "- Older rules consolidated: release every resource on every code path.\n"
            + appendedSection;

        var agentRunner = new MockAgentRunner
        {
            // The overflow is the IMPROVER'S OWN edit, written from inside the agent callback —
            // i.e. AFTER the pre-run baseline preparation, whose `clean -fdx` deliberately
            // clears untracked residue. Pre-seeding it before ExecuteAsync would describe
            // residue that preparation legitimately removes, not the agent's work.
            PromptResponder = async (prompt, _, ct) =>
            {
                if (prompt.Contains("append-new/compress-old policy", StringComparison.Ordinal))
                {
                    await File.WriteAllTextAsync(filePath, repaired, ct);
                }
                else
                {
                    await File.WriteAllTextAsync(filePath, oversized, ct);
                    // The same observable on-disk state the fixture used to pre-seed, produced
                    // at the point in the flow where the improver would really produce it.
                    Assert.Equal(observedLength, File.ReadAllText(filePath).Length);
                }

                return "Mock agent response";
            },
        };
        var git = new MockGitOperations();
        var executor = new TaskExecutor(agentRunner, gitOperations: git, configRepoDir: configRepoDir);

        await executor.ExecuteAsync(
            BuildImproverTask("improver-overflow-policy"), TestContext.Current.CancellationToken);

        Assert.Equal(2, agentRunner.PromptCalls.Count);
        var retry = agentRunner.PromptCalls[1];
        var normalizedRetryPrompt = string.Join(' ',
            retry.Prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(Path.Combine(configRepoDir, "agents"), retry.WorkDir);
        Assert.Contains($"{fileName}: {observedLength} characters", retry.Prompt, StringComparison.Ordinal);
        Assert.Contains($"limit: {WorkerConstants.AgentsMdMaxCharacters}", retry.Prompt, StringComparison.Ordinal);
        Assert.Contains($"within {WorkerConstants.AgentsMdMaxCharacters} characters", retry.Prompt, StringComparison.Ordinal);
        Assert.Contains("Work from the TOP of the existing material downward", retry.Prompt, StringComparison.Ordinal);
        Assert.Contains("first consolidate and compress", retry.Prompt, StringComparison.Ordinal);
        Assert.Contains("remove the oldest material", retry.Prompt, StringComparison.Ordinal);
        Assert.Contains("lessons you appended in this session untouched", retry.Prompt, StringComparison.Ordinal);
        Assert.Contains("do not edit, reword, reorder or delete them", normalizedRetryPrompt, StringComparison.Ordinal);
        Assert.Contains("Do NOT add new content or new lessons", retry.Prompt, StringComparison.Ordinal);
        Assert.Contains("protected safety constraints", retry.Prompt, StringComparison.Ordinal);
        Assert.Contains("readable ordinary-language bullets", retry.Prompt, StringComparison.Ordinal);
        Assert.Contains("stop and report", retry.Prompt, StringComparison.Ordinal);
        Assert.Contains("blocker", retry.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Prioritize the most impactful rules", retry.Prompt, StringComparison.Ordinal);

        // Defect 1: the retry must explicitly forbid meeting the cap by truncating a whole file.
        Assert.Contains("Never meet the limit by truncating a whole file", normalizedRetryPrompt, StringComparison.Ordinal);
        Assert.Contains("do not cut the file short", normalizedRetryPrompt, StringComparison.Ordinal);
        Assert.Contains("do not replace it with a stub", normalizedRetryPrompt, StringComparison.Ordinal);

        // Defect 2: the retry must require the compressed result to remain readable to a new
        // reader, with useful headings — not just bullets.
        Assert.Contains("readable to a new reader", normalizedRetryPrompt, StringComparison.Ordinal);
        Assert.Contains("keep useful headings and concise ordinary-language bullets", normalizedRetryPrompt, StringComparison.Ordinal);
        Assert.Contains("opening the file for the first time", normalizedRetryPrompt, StringComparison.Ordinal);

        // The accepted repair compresses from the top instead of truncating: the heading survives,
        // the appended lesson is byte-for-byte intact, and the file is not a stub.
        var finalContent = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);
        Assert.Equal(repaired, finalContent);
        Assert.StartsWith(heading, finalContent, StringComparison.Ordinal);
        Assert.EndsWith(appendedSection, finalContent, StringComparison.Ordinal);
        Assert.True(finalContent.Length <= WorkerConstants.AgentsMdMaxCharacters);
        Assert.DoesNotContain("checkout -- agents/", git.GitCommands);
    }

    /// <summary>
    /// Enforcement uses the fixed 8,000-character product boundary: 7,999 and 8,000 UTF-16
    /// code units pass untouched, while 8,001 causes one successful compression prompt. The
    /// explicit 8,000 vector is removal-proof against restoring the former 4,000-character cap.
    /// </summary>
    [Theory]
    [InlineData(7_999, false)]
    [InlineData(8_000, false)]
    [InlineData(8_001, true)]
    public async Task ExecuteAsync_ImproverAgentsMdBoundary_EnforcesOnlyAboveEightThousand(
        int characterCount, bool expectsCompression)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var filePath = Path.Combine(configRepoDir, "agents", "boundary.agents.md");

        var agentRunner = new MockAgentRunner
        {
            // Written from the agent callback (post-preparation), not pre-seeded: the pre-run
            // baseline restore clears untracked residue, so the boundary content must be the
            // improver's own edit for the size check to describe the right thing.
            PromptResponder = async (prompt, _, ct) =>
            {
                if (prompt.Contains("append-new/compress-old policy", StringComparison.Ordinal))
                {
                    await File.WriteAllTextAsync(filePath, "repaired", ct);
                }
                else
                {
                    await File.WriteAllTextAsync(filePath, new string('a', characterCount), ct);
                    Assert.Equal(characterCount, File.ReadAllText(filePath).Length);
                }

                return "Mock agent response";
            },
        };
        var git = new MockGitOperations();
        var executor = new TaskExecutor(agentRunner, gitOperations: git, configRepoDir: configRepoDir);

        await executor.ExecuteAsync(
            BuildImproverTask($"improver-boundary-{characterCount}"),
            TestContext.Current.CancellationToken);

        var compressionPrompts = agentRunner.PromptCalls.Count(call =>
            call.Prompt.Contains("append-new/compress-old policy", StringComparison.Ordinal));
        Assert.Equal(expectsCompression ? 1 : 0, compressionPrompts);
        Assert.DoesNotContain("checkout -- agents/", git.GitCommands);
    }

    /// <summary>
    /// A file containing surrogate pairs can exceed 8,000 UTF-8 bytes while remaining exactly
    /// 8,000 UTF-16 code units. It must pass without compression, proving the cap is not byte-based.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ImproverNonAsciiBoundary_CountsUtf16CodeUnitsNotUtf8Bytes()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var filePath = Path.Combine(configRepoDir, "agents", "unicode-boundary.agents.md");
        var content = string.Concat(Enumerable.Repeat("😀", 4_000));

        var agentRunner = new MockAgentRunner
        {
            // Written from the agent callback (post-preparation): the pre-run baseline restore
            // clears untracked residue, so the file must be the improver's own edit.
            PromptResponder = async (_, _, ct) =>
            {
                await File.WriteAllTextAsync(filePath, content, ct);

                // The SAME observable on-disk state the fixture used to pre-seed, asserted at
                // the point in the flow where the improver would really produce it.
                var written = await File.ReadAllTextAsync(filePath, ct);
                Assert.Equal(8_000, written.Length);
                Assert.Equal(WorkerConstants.AgentsMdMaxCharacters, written.Length);
                Assert.Equal(16_000, System.Text.Encoding.UTF8.GetByteCount(written));
                Assert.Equal(16_000, new FileInfo(filePath).Length);

                return "Mock agent response";
            },
        };
        var git = new MockGitOperations();
        var executor = new TaskExecutor(agentRunner, gitOperations: git, configRepoDir: configRepoDir);

        await executor.ExecuteAsync(
            BuildImproverTask("improver-unicode-boundary"), TestContext.Current.CancellationToken);

        // The final on-disk state is still the 8,000-code-unit / 16,000-byte file: enforcement
        // neither compressed nor discarded it.
        var readBack = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);
        Assert.Equal(8_000, readBack.Length);
        Assert.Equal(16_000, System.Text.Encoding.UTF8.GetByteCount(readBack));

        Assert.Single(agentRunner.PromptCalls);
        Assert.DoesNotContain(agentRunner.PromptCalls, call =>
            call.Prompt.Contains("append-new/compress-old policy", StringComparison.Ordinal));
        Assert.DoesNotContain("checkout -- agents/", git.GitCommands);
    }

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
    /// TRUTHFUL PUBLICATION: the failed push is additionally a truthful Failed/FAIL outcome, with
    /// the accumulated agent output preserved verbatim and the sanitized reason appended.
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

        // TRUTHFUL PUBLICATION: a failed push is a publication failure, not a no-change pass.
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Contains(result.Metrics.Issues, i => i.Contains("git push failed"));

        // The accumulated agent output is preserved VERBATIM with the sanitized reason appended.
        Assert.StartsWith("Mock agent response", result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Git Failure]", result.Output);
        Assert.Contains("git push failed (exit 1): remote rejected: permission denied", result.Output);

        // Path-resolution assertions: git work dir, agent work dir, and prompt context all resolve to the injected agents path.
        var expectedAgentsDir = Path.Combine(configRepoDir, "agents");
        Assert.Contains(configRepoDir, git.WorkDirs);
        Assert.Equal(expectedAgentsDir, agentRunner.LastWorkDir);
        Assert.Contains($"Working directory: {expectedAgentsDir}", agentRunner.LastPrompt);
    }

    /// <summary>
    /// When the improver's COMMIT fails, the paths must still be reported, and the commit
    /// failure is a truthful Failed/FAIL publication outcome — never a silent pass.
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

        // TRUTHFUL PUBLICATION: failed commit stops ALL subsequent publication commands —
        // no post-commit pull, no merge --abort, and no push (full subsequence absence via
        // the shared helper, so a stray merge-abort would fail this test).
        AssertLegacyStoppedAfterCommit(git.GitCommands);
        // And the exact captured sequence proves nothing beyond the failed commit launched —
        // followed by the step-end cleanup (whose restore target is the captured baseline).
        Assert.Equal(
            [
                .. ConfigRepoPreparationFakes.LegacyCommands,
                "add agents/*.agents.md",
                "diff --cached --name-only -z",
                $"commit -m \"{ImproverCommitMessage}\"",
                .. ConfigRepoPreparationFakes.LegacyCleanupCommands,
            ],
            git.GitCommands);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("git commit failed (exit 1)") && i.Contains("hook rejected"));

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
        // early (Directory.Exists check) — no real agents.md files are read. A .git MARKER
        // is still created so preparation succeeds and the prompt is delivered: with the
        // truthful-preparation change a non-repository directory FAILS the task before
        // the agent is ever prompted.
        var tempConfigRepo = Path.Combine(Path.GetTempPath(), $"test-config-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempConfigRepo, ".git"));
        Directory.CreateDirectory(Path.Combine(tempConfigRepo, "agents"));
        using var remover = new DirectoryRemover(tempConfigRepo);
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

        // The .git marker exists, so preparation succeeds and the (PASS) default verdict holds.
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

        /// <summary>
        /// The exit code the <c>check-ref-format</c> subprocess reports. Defaults to 0 (a valid
        /// ref); a non-zero value models git's own rejection of a malformed ref, which is what
        /// the preflight's authoritative validation consumes.
        /// </summary>
        public int CheckRefFormatExitCode { get; set; }

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
                return Task.FromResult(new GitProcessResult(CheckRefFormatExitCode, "", ""));

            // A test's own Responder always wins; otherwise the Improver preparation commands
            // get EXPLICIT valid answers (root/SHA/branch/upstream/empty status). Exit-zero with
            // an empty stdout is never a usable baseline, so the fallthrough must not supply one.
            return Task.FromResult(
                Responder?.Invoke(tokens)
                ?? ConfigRepoPreparationFakes.SeamAnswer(request.WorkingDirectory, tokens)
                ?? new GitProcessResult(0, "", ""));
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
        CancellationToken? ct = null,
        MockAgentRunner? agentRunner = null,
        string model = "")
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
                agentRunner ?? new MockAgentRunner(), null, git, null, configRepoDir, seam);
            var result = await executor.ExecuteAsync(
                BuildImproverTask(taskId, model), ct ?? TestContext.Current.CancellationToken);

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
        string taskId, string configRepoDir, MockGitOperations git, CancellationToken? ct = null,
        MockAgentRunner? agentRunner = null, string model = "")
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
                agentRunner ?? new MockAgentRunner(), gitOperations: git, configRepoDir: configRepoDir);
            var result = await executor.ExecuteAsync(
                BuildImproverTask(taskId, model), ct ?? TestContext.Current.CancellationToken);

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

    // ── Stage-stop coverage helpers ───────────────────────────────────────────
    //
    // A stage failure must stop EVERY subsequent publication command, not merely the
    // immediately following one. Each helper asserts the full forbidden subsequence is
    // absent on its command-routing path, so removing the protection named by a test
    // makes the test fail.

    /// <summary>After a failed ADD: no diff, no commit, no post-commit pull, no merge --abort, no push.</summary>
    private static void AssertLegacyStoppedAfterAdd(List<string> commands)
    {
        Assert.DoesNotContain(commands, c => c.StartsWith("diff --cached", StringComparison.Ordinal));
        AssertLegacyStoppedAfterDiff(commands);
    }

    /// <summary>After a failed DIFF: no commit, no post-commit pull, no merge --abort, no push.</summary>
    private static void AssertLegacyStoppedAfterDiff(List<string> commands)
    {
        Assert.DoesNotContain(commands, c => c.StartsWith("commit -m", StringComparison.Ordinal));
        AssertLegacyStoppedAfterCommit(commands);
    }

    /// <summary>After a failed COMMIT: no post-commit pull, no merge --abort, no push.</summary>
    private static void AssertLegacyStoppedAfterCommit(List<string> commands)
    {
        Assert.DoesNotContain(commands, c => c == "pull --no-rebase");
        Assert.DoesNotContain(commands, c => c == "merge --abort");
        Assert.DoesNotContain(commands, c => c == "push");
    }

    /// <summary>SEAM form of <see cref="AssertLegacyStoppedAfterAdd"/>: additionally no second add.</summary>
    private static void AssertSeamStoppedAfterAdd(List<string[]> launched)
    {
        Assert.DoesNotContain(launched, t => t is ["diff", ..]);
        AssertSeamStoppedAfterDiff(launched);
    }

    /// <summary>SEAM form of <see cref="AssertLegacyStoppedAfterDiff"/>.</summary>
    private static void AssertSeamStoppedAfterDiff(List<string[]> launched)
    {
        Assert.DoesNotContain(launched, t => t is ["commit", ..]);
        AssertSeamStoppedAfterCommit(launched);
    }

    /// <summary>SEAM form of <see cref="AssertLegacyStoppedAfterCommit"/>.</summary>
    private static void AssertSeamStoppedAfterCommit(List<string[]> launched)
    {
        Assert.DoesNotContain(launched, t => t is ["pull", "--no-rebase", ..]);
        Assert.DoesNotContain(launched, t => t is ["merge", "--abort"]);
        Assert.DoesNotContain(launched, t => t is ["push", ..]);
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

        // The COMPLETE launch sequence: the pre-run baseline preparation (preflight → fetch →
        // resolve → destructive restore → verify), then the publication — including the
        // publication-HEAD resolution BEFORE the push and the seam's explicit-origin
        // canonicalization of the positional-free post-commit pull — then the step-end
        // cleanup (finalization) with the confirmed publication SHA as the reset target.
        AssertLaunchedSequence(fake,
            [
                .. ConfigRepoPreparationFakes.SeamLaunches,
                ["add", "agents/*.agents.md"],
                ["diff", "--cached", "--name-only", "-z"],
                ["commit", "-m", ImproverCommitMessage],
                ["remote", "get-url", "origin"],
                ["pull", "--no-rebase", "origin"],
                ["rev-parse", "--verify", "HEAD^{commit}"],
                ["check-ref-format", "--allow-onelevel", "HEAD"],
                ["remote", "get-url", "origin"],
                ["push", "origin", "HEAD"],
                .. ConfigRepoPreparationFakes.SeamCleanupLaunches,
            ]);

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

        // Stage 6a runs for the THREE transport commands only (the preparation fetch, the
        // post-commit pull and the push) — every local command, including the preflight and
        // cleanup rev-parse forms, the publication-HEAD probe, and the reset/clean/status,
        // never resolves the URL.
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
            ["fetch", "origin", ConfigRepoPreparationFakes.BranchRef],
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
        // local command — including every cleanup command — never reaches Stage 6d.
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
            [
                .. ConfigRepoPreparationFakes.SeamLaunchesWithOriginRepair(repair),
                ["add", "agents/*.agents.md"],
                ["diff", "--cached", "--name-only", "-z"],
                // The step-end cleanup: the empty staged diff means no confirmed publication,
                // so the restore target is the captured fetched baseline.
                .. ConfigRepoPreparationFakes.SeamCleanupLaunches,
            ]);
    }

    /// <summary>
    /// A <see cref="MockAgentRunner"/> that writes one valid edit and one persistently oversized
    /// guidance file from its initial prompt, then returns a distinct segment for every
    /// condensation retry without repairing the violation.
    /// </summary>
    private static MockAgentRunner ExhaustingAgentsFileWriter(
        string configRepoDir,
        IReadOnlyList<string> segments,
        TestResultReport? incidentalReport = null)
    {
        var call = 0;
        return new MockAgentRunner
        {
            TestReportToReturn = incidentalReport,
            PromptResponder = async (_, _, ct) =>
            {
                if (call == 0)
                {
                    Directory.CreateDirectory(Path.Combine(configRepoDir, "agents"));
                    await File.WriteAllTextAsync(
                        Path.Combine(configRepoDir, "agents", "valid-edit.agents.md"),
                        "otherwise valid guidance edit", ct);
                    await File.WriteAllTextAsync(
                        Path.Combine(configRepoDir, "agents", "blocker.agents.md"),
                        new string('x', WorkerConstants.AgentsMdMaxCharacters + 7), ct);
                }

                return segments[call++];
            },
        };
    }

    private static string ExhaustionWarning(string fileName, int characterCount) => $"""
        [WARNING: agents.md guidance update SKIPPED — compression retries exhausted]
        After the initial prompt and {WorkerConstants.AgentsMdMaxRetries} condensation retries the following
        file(s) still exceed the {WorkerConstants.AgentsMdMaxCharacters}-character limit:
          - {fileName}: {characterCount} characters (limit: {WorkerConstants.AgentsMdMaxCharacters})
        No agents.md change was published: the entire guidance update was skipped, including
        edits to files that are within the limit. No add, staged diff, commit, pull or push
        was performed for this Improver run.
        """;

    /// <summary>
    /// Persistent overflow exhausts exactly three retries on BOTH config-Git routes. The whole
    /// mixed update is skipped, every response is selected verbatim and in order, an incidental
    /// FAIL test report cannot override SKIP, and no publication command (including the deleted
    /// checkout-discard command) is issued.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Improver_ExhaustedCompression_PublishesNothingAndReportsAuthoritativeSkip(bool viaSeam)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        string[] segments =
        [
            "INITIAL:\nreviewed every file verbatim",
            "RETRY-ONE:\nprotected constraints still do not fit",
            "RETRY-TWO:\nolder rules were consolidated but remain too large",
            "RETRY-THREE:\nfinal blocker report, no content sacrificed",
        ];
        var incidental = new TestResultReport
        {
            Verdict = TaskVerdict.Fail,
            BuildSuccess = false,
            TotalTests = 9,
            PassedTests = 2,
            FailedTests = 7,
            Summary = "INCIDENTAL TEST SUMMARY MUST NOT WIN",
            Issues = ["incidental test issue"],
        };
        var runner = ExhaustingAgentsFileWriter(configRepoDir, segments, incidental);
        var git = new MockGitOperations();
        TaskResult result;

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake();
            using var seam = CreateConfigRepoSeam(configRepoDir);
            (result, _, _) = await RunImproverWithSeamAsync(
                "improver-seam-exhausted", configRepoDir, seam, fake, git,
                agentRunner: runner);

            AssertLaunchedSequence(fake,
                [.. ConfigRepoPreparationFakes.SeamLaunches,
                 .. ConfigRepoPreparationFakes.SeamCleanupLaunches]);
            AssertNoPublicationLaunched(fake);
            Assert.Empty(git.GitCommands);
        }
        else
        {
            (result, _, _) = await RunImproverLegacyAsync(
                "improver-legacy-exhausted", configRepoDir, git, agentRunner: runner);

            Assert.Equal(
                [.. ConfigRepoPreparationFakes.LegacyCommands,
                 .. ConfigRepoPreparationFakes.LegacyCleanupCommands],
                git.GitCommands);
            AssertNoLegacyPublication(git);
        }

        Assert.Equal(1 + WorkerConstants.AgentsMdMaxRetries, runner.PromptCalls.Count);
        Assert.All(runner.PromptCalls.Skip(1), call =>
            Assert.Contains("append-new/compress-old policy", call.Prompt, StringComparison.Ordinal));

        var warning = ExhaustionWarning(
            "blocker.agents.md", WorkerConstants.AgentsMdMaxCharacters + 7);
        var expected = string.Join("\n\n",
            segments[0],
            "[Agents.md size enforcement]\n" + segments[1],
            "[Agents.md size enforcement]\n" + segments[2],
            "[Agents.md size enforcement]\n" + segments[3],
            warning);

        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Equal("SKIP", result.Metrics!.Verdict);
        Assert.Equal(expected, result.Output);
        Assert.Equal(expected, result.Metrics.Summary);
        Assert.DoesNotContain("INCIDENTAL TEST SUMMARY", result.Metrics.Summary, StringComparison.Ordinal);
        Assert.Equal(9, result.Metrics.TotalTests);
        Assert.Contains("incidental test issue", result.Metrics.Issues);
        Assert.Contains(warning, result.Metrics.Issues);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(0, result.GitStatus.FilesChanged);
        Assert.Empty(result.GitStatus.ChangedFiles);

        // The valid edit genuinely coexists with the remaining oversized blocker. Command
        // absence therefore proves the blocker skipped the ENTIRE update, not only itself.
        Assert.Equal(
            "otherwise valid guidance edit",
            await File.ReadAllTextAsync(
                Path.Combine(configRepoDir, "agents", "valid-edit.agents.md"),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            WorkerConstants.AgentsMdMaxCharacters + 7,
            (await File.ReadAllTextAsync(
                Path.Combine(configRepoDir, "agents", "blocker.agents.md"),
                TestContext.Current.CancellationToken)).Length);
    }

    /// <summary>
    /// A repair returned by the THIRD condensation retry is still success. Both routes must run
    /// all four prompts and then publish, rather than misclassifying the final repair as SKIP.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Improver_FinalCompressionRetryRepairsViolation_PublicationSucceeds(bool viaSeam)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var filePath = Path.Combine(configRepoDir, "agents", "final-retry.agents.md");
        var call = 0;
        var runner = new MockAgentRunner
        {
            PromptResponder = async (_, _, ct) =>
            {
                call++;
                await File.WriteAllTextAsync(
                    filePath,
                    call == 4 ? "repaired on final retry" : new string('x', 8_001),
                    ct);
                return $"segment-{call}";
            },
        };
        var git = new MockGitOperations();
        TaskResult result;

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake
            {
                Responder = tokens => tokens is ["diff", ..]
                    ? new GitProcessResult(0, StagedOutput("agents/final-retry.agents.md"), "")
                    : null,
            };
            using var seam = CreateConfigRepoSeam(configRepoDir);
            (result, _, _) = await RunImproverWithSeamAsync(
                "improver-seam-final-retry", configRepoDir, seam, fake, git,
                agentRunner: runner);

            Assert.Contains(fake.Launched, tokens => tokens is ["add", "agents/*.agents.md"]);
            Assert.Contains(fake.Launched, tokens => tokens is ["commit", ..]);
            Assert.Contains(fake.Launched, tokens => tokens is ["pull", "--no-rebase", ..]);
            Assert.Contains(fake.Launched, tokens => tokens is ["push", "origin", "HEAD"]);
        }
        else
        {
            git.GitCommandResponder = ConfigRepoResponder(
                ["agents/final-retry.agents.md"], pushFails: false);
            (result, _, _) = await RunImproverLegacyAsync(
                "improver-legacy-final-retry", configRepoDir, git, agentRunner: runner);

            Assert.Contains("add agents/*.agents.md", git.GitCommands);
            Assert.Contains(git.GitCommands, command => command.StartsWith("commit -m", StringComparison.Ordinal));
            Assert.Contains("pull --no-rebase", git.GitCommands);
            Assert.Contains("push", git.GitCommands);
        }

        Assert.Equal(4, runner.PromptCalls.Count);
        Assert.Equal("repaired on final retry", await File.ReadAllTextAsync(
            filePath, TestContext.Current.CancellationToken));
        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Equal("PASS", result.Metrics!.Verdict);
        Assert.True(result.GitStatus!.Pushed);
        Assert.DoesNotContain("guidance update SKIPPED", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Exhaustion followed by cleanup failure preserves the complete SKIP warning and agent
    /// evidence, but truthfully downgrades the completed outcome to Failed/FAIL. Both config-Git
    /// routes are exercised without copying the finalizer's wider matrix.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Improver_ExhaustionWithCleanupFailure_PreservesEvidenceAndReturnsFail(bool viaSeam)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        string[] segments = ["initial-evidence", "retry-one", "retry-two", "retry-three"];
        var runner = ExhaustingAgentsFileWriter(configRepoDir, segments);
        var warning = ExhaustionWarning("blocker.agents.md", 8_007);
        var git = new MockGitOperations();
        TaskResult result;

        if (viaSeam)
        {
            var statusCalls = 0;
            var fake = new SeamProcessRunnerFake
            {
                Responder = tokens => tokens is ["status", "--porcelain=v1", ..]
                    ? new GitProcessResult(0, ++statusCalls == 1 ? "" : "?? cleanup-residue.txt\n", "")
                    : null,
            };
            using var seam = CreateConfigRepoSeam(configRepoDir);
            (result, _, _) = await RunImproverWithSeamAsync(
                "improver-seam-exhaust-cleanup-fail", configRepoDir, seam, fake, git,
                agentRunner: runner);
            AssertNoPublicationLaunched(fake);
        }
        else
        {
            var statusCalls = 0;
            git.GitCommandResponder = args =>
                args == "status --porcelain=v1 --untracked-files=all --ignored"
                    ? (0, ++statusCalls == 1 ? "" : "?? cleanup-residue.txt\n", "")
                    : null;
            (result, _, _) = await RunImproverLegacyAsync(
                "improver-legacy-exhaust-cleanup-fail", configRepoDir, git,
                agentRunner: runner);
            AssertNoLegacyPublication(git);
        }

        Assert.Equal(4, runner.PromptCalls.Count);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
        Assert.StartsWith("initial-evidence", result.Output, StringComparison.Ordinal);
        Assert.Contains("retry-three", result.Output, StringComparison.Ordinal);
        Assert.Contains(warning, result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Cleanup Failure]", result.Output, StringComparison.Ordinal);
        Assert.Contains(warning, result.Metrics.Issues);
        Assert.Contains(result.Metrics.Issues,
            issue => issue.Contains("Config repo cleanup rejected", StringComparison.Ordinal));
    }

    /// <summary>
    /// Requested cancellation during a later retry or by the FINAL returned condensation response
    /// is always Cancelled/CANCELLED, never a benign exhausted SKIP. Each timing is proven on both
    /// routes; the final-response case verifies the explicit token observation after retry three.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Improver_RequestedCancellationDuringLaterCompression_NeverReportsSkip(
        bool viaSeam, bool cancelByFinalResponse)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        using var cts = new CancellationTokenSource();
        var filePath = Path.Combine(configRepoDir, "agents", "cancelled.agents.md");
        var call = 0;
        var runner = new MockAgentRunner
        {
            PromptResponder = async (_, _, ct) =>
            {
                call++;
                if (call == 1)
                    await File.WriteAllTextAsync(filePath, new string('x', 8_001), ct);

                if ((!cancelByFinalResponse && call == 3) || (cancelByFinalResponse && call == 4))
                {
                    cts.Cancel();
                    if (!cancelByFinalResponse)
                        throw new OperationCanceledException("shutdown", cts.Token);
                }

                return $"cancel-segment-{call}";
            },
        };
        var git = new MockGitOperations();
        TaskResult result;

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake();
            using var seam = CreateConfigRepoSeam(configRepoDir);
            (result, _, _) = await RunImproverWithSeamAsync(
                $"improver-seam-compression-cancel-{cancelByFinalResponse}",
                configRepoDir, seam, fake, git, cts.Token, runner);
            AssertNoPublicationLaunched(fake);
        }
        else
        {
            (result, _, _) = await RunImproverLegacyAsync(
                $"improver-legacy-compression-cancel-{cancelByFinalResponse}",
                configRepoDir, git, cts.Token, runner);
            AssertNoLegacyPublication(git);
        }

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(cancelByFinalResponse ? 4 : 3, runner.PromptCalls.Count);
        Assert.Equal(TaskOutcome.Cancelled, result.Status);
        Assert.Equal("CANCELLED", result.Metrics!.Verdict);
        Assert.NotEqual("SKIP", result.Metrics.Verdict);
        Assert.Contains("cancel-segment-1", result.Output, StringComparison.Ordinal);
        if (cancelByFinalResponse)
            Assert.Contains("cancel-segment-4", result.Output, StringComparison.Ordinal);
        Assert.Contains("Task was cancelled.", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("guidance update SKIPPED", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A FAILING <c>pull --no-rebase</c> aborts the merge — through the seam, with the tokenized
    /// <c>merge --abort</c> between them — and push is NEVER attempted, whether the abort
    /// succeeds, fails, or throws. The task ends in a truthful Failed/FAIL outcome.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_FailedPull_AbortsMergeAndNeverPushes()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens switch
            {
                ["diff", ..] => new GitProcessResult(0, StagedOutput("agents/coder.agents.md"), ""),
                ["pull", "--no-rebase", ..] => new GitProcessResult(1, "", "merge conflict"),
                _ => null, // merge --abort succeeds (exit 0)
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();

        var (result, _, stderr) = await RunImproverWithSeamAsync(
            "improver-seam-merge-abort", configRepoDir, seam, fake, git);

        // The push (and its check-ref-format / origin inspection preamble) is ABSENT.
        // The step-end cleanup follows the merge abort: no confirmed push, so the captured
        // fetched baseline is the restore target.
        AssertLaunchedSequence(fake,
            [
                .. ConfigRepoPreparationFakes.SeamLaunches,
                ["add", "agents/*.agents.md"],
                ["diff", "--cached", "--name-only", "-z"],
                ["commit", "-m", ImproverCommitMessage],
                ["remote", "get-url", "origin"],
                ["pull", "--no-rebase", "origin"],
                ["merge", "--abort"],
                .. ConfigRepoPreparationFakes.SeamCleanupLaunches,
            ]);

        Assert.Contains("git pull failed: merge conflict", FindLine(stderr, "git pull failed"), StringComparison.Ordinal);

        // TRUTHFUL PUBLICATION: failed pull → Failed outcome, FAIL verdict, no Pushed claim.
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(["agents/coder.agents.md"], result.GitStatus.ChangedFiles);
        Assert.Equal(1, result.GitStatus.FilesChanged);
        Assert.Contains(result.Metrics.Issues, i => i.Contains("git pull failed (exit 1)") && i.Contains("push not attempted"));
    }

    /// <summary>
    /// A FAILING <c>pull --no-rebase</c> followed by a THROWING <c>merge --abort</c>: the abort's
    /// exception must not lose the pull failure, and push is still NEVER attempted.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_FailedPull_ThrowingMergeAbort_StillNeverPushes()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens switch
            {
                ["diff", ..] => new GitProcessResult(0, StagedOutput("agents/coder.agents.md"), ""),
                ["pull", "--no-rebase", ..] => new GitProcessResult(1, "", "merge conflict"),
                ["merge", "--abort"] => throw new InvalidOperationException("abort exploded"),
                _ => null,
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-abort-throw", configRepoDir, seam, fake, git);

        Assert.DoesNotContain(fake.Launched, t => t is ["push", ..]);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Contains(result.Metrics.Issues, i => i.Contains("git pull failed (exit 1)"));
    }

    /// <summary>
    /// A FAILING <c>pull --no-rebase</c> followed by a FAILING <c>merge --abort</c>: push is
    /// still NEVER attempted and the outcome is a truthful failure.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_FailedPull_FailingMergeAbort_StillNeverPushes()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens switch
            {
                ["diff", ..] => new GitProcessResult(0, StagedOutput("agents/coder.agents.md"), ""),
                ["pull", "--no-rebase", ..] => new GitProcessResult(1, "", "merge conflict"),
                ["merge", "--abort"] => new GitProcessResult(3, "", "abort refused"),
                _ => null,
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-abort-fail", configRepoDir, seam, fake, git);

        Assert.DoesNotContain(fake.Launched, t => t is ["push", ..]);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
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
                .. ConfigRepoPreparationFakes.LegacyCommands,
                "add agents/*.agents.md",
                "diff --cached --name-only -z",
                $"commit -m \"{ImproverCommitMessage}\"",
                "pull --no-rebase",
                // The publication HEAD is resolved and validated BEFORE the push.
                "rev-parse --verify HEAD^{commit}",
                "push",
                // The step-end cleanup (finalization) with the publication SHA as target.
                .. ConfigRepoPreparationFakes.LegacyCleanupCommands,
            ],
            git.GitCommands);
        Assert.All(git.WorkDirs, dir => Assert.Equal(configRepoDir, dir));
    }

    /// <summary>
    /// The legacy failed-pull/merge-abort behavior remains independently covered with a
    /// NON-OVERFLOW runner. Limits are satisfied, so publication reaches the failed pull,
    /// launches the exact opaque merge-abort command, and never pushes.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_FailedPull_UsesExactOpaqueMergeAbortAndNeverPushes()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                "diff --cached --name-only -z" => (0, StagedOutput("agents/coder.agents.md"), ""),
                "pull --no-rebase" => (1, "", "merge conflict"),
                _ => null,
            },
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-abort", configRepoDir, git,
            agentRunner: new MockAgentRunner());

        Assert.Equal(
            [
                .. ConfigRepoPreparationFakes.LegacyCommands,
                "add agents/*.agents.md",
                "diff --cached --name-only -z",
                $"commit -m \"{ImproverCommitMessage}\"",
                "pull --no-rebase",
                "merge --abort",
                .. ConfigRepoPreparationFakes.LegacyCleanupCommands,
            ],
            git.GitCommands);

        Assert.DoesNotContain("checkout -- agents/", git.GitCommands);
        Assert.DoesNotContain("push", git.GitCommands);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
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
        // The PREPARATION and PUBLICATION launches forward the execution token verbatim; the
        // step-end CLEANUP runs on its own independent 30-second budget — never the execution
        // token — so its launches carry a DIFFERENT token.
        var preparationAndPublicationCount =
            ConfigRepoPreparationFakes.SeamLaunches.Length + 9; // add/diff/commit/remote/pull/pubHEAD/ref/remote/push
        Assert.Equal(
            preparationAndPublicationCount + ConfigRepoPreparationFakes.SeamCleanupLaunches.Length,
            fake.Tokens.Count);
        for (var i = 0; i < preparationAndPublicationCount; i++)
            Assert.Equal(cts.Token, fake.Tokens[i]);
        for (var i = preparationAndPublicationCount; i < fake.Tokens.Count; i++)
            Assert.NotEqual(cts.Token, fake.Tokens[i]);
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
        // The PREPARATION and PUBLICATION commands forward the execution token verbatim; the
        // step-end CLEANUP runs on its own independent 30-second budget — never the execution
        // token — so its seven commands carry a DIFFERENT token.
        var preparationAndPublicationCount =
            ConfigRepoPreparationFakes.LegacyCommands.Length + 6; // add/diff/commit/pull/pubHEAD/push
        Assert.Equal(preparationAndPublicationCount, ConfigRepoPreparationFakes.LegacyCommands.Length + 6);
        Assert.Equal(
            preparationAndPublicationCount + ConfigRepoPreparationFakes.LegacyCleanupCommands.Length,
            git.GitTokens.Count);
        for (var i = 0; i < preparationAndPublicationCount; i++)
            Assert.Equal(cts.Token, git.GitTokens[i]);
        for (var i = preparationAndPublicationCount; i < git.GitTokens.Count; i++)
            Assert.NotEqual(cts.Token, git.GitTokens[i]);
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
    /// TRUTHFUL PREPARATION: the <c>Directory.Exists(.git)</c> absence check FAILS the task
    /// BEFORE the agent is prompted — a worker must never be given permission to edit a
    /// non-repository directory. NEITHER the pull NOR the commit/push sequence launches
    /// anything, and the result is a truthful Failed outcome with a FAIL verdict (never a
    /// normal no-change completion).
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
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-nogit", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        Assert.Empty(fake.Requests);
        Assert.Empty(git.GitCommands);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Contains(result.Metrics.Issues, i => i.Contains("Config repo not found"));
        // The agent was never prompted — no permission to edit a non-repository directory.
        Assert.Empty(agentRunner.PromptCalls);
    }

    /// <summary>
    /// The uniform exit-code handling: a NON-ZERO seam result renders the preserved
    /// <c>Config repo preparation … failed (exit N)</c> wording with the seam's exit code.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_NonZeroFetch_LogsThePreservedExitCodeWording()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens switch
            {
                ["fetch", ..] => new GitProcessResult(3, "", "could not fetch"),
                ["diff", ..] => new GitProcessResult(0, "", ""),
                _ => null,
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();

        var (_, _, stderr) = await RunImproverWithSeamAsync(
            "improver-seam-exit3", configRepoDir, seam, fake, git);

        Assert.Contains(
            "Config repo preparation fetch failed (exit 3): could not fetch",
            FindLine(stderr, "Config repo preparation fetch failed"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A seam REJECTION (Stage 6a: the resolved URL is absent) during PREPARATION stops the
    /// Improver path truthfully: the rejection flows into the SAME error log lines as a legacy
    /// non-zero exit, carrying the seam's fixed <c>SanitizedError</c> and its <c>-1</c> exit
    /// code — and the task FAILS before the agent is ever prompted.
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
        // No resolved URL: every TRANSPORT command is rejected at Stage 6a — including the
        // PREPARATION fetch, which truthfully fails the task.
        using var seam = CreateConfigRepoSeam(configRepoDir, resolvedUrlResolver: static () => null);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, stderr) = await RunImproverWithSeamAsync(
            "improver-seam-reject", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        Assert.Contains(
            "Config repo preparation fetch failed (exit -1): Config repo URL is not available.",
            FindLine(stderr, "Config repo preparation fetch failed"),
            StringComparison.Ordinal);

        // The local preflight ran (it never resolves a URL) — including its own authoritative
        // check-ref-format for the discovered ref — then the rejected fetch stopped everything:
        // no reset, no clean, no post-restore verification and no publication.
        AssertLaunchedSequence(fake,
            ["rev-parse", "--show-toplevel"],
            ["rev-parse", "--verify", "HEAD^{commit}"],
            ["rev-parse", "--symbolic-full-name", "HEAD"],
            ["check-ref-format", "--allow-onelevel", ConfigRepoPreparationFakes.BranchRef],
            ["rev-parse", "--symbolic-full-name", "@{upstream}"]);

        // TRUTHFUL PREPARATION: the task fails BEFORE the agent is prompted.
        Assert.Empty(agentRunner.PromptCalls);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("Config repo preparation fetch failed (exit -1)"));
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

    // ── (f) Truthful preparation/publication failure semantics ───────────────

    /// <summary>
    /// TRUTHFUL PREPARATION (legacy path): a non-zero preparation FETCH FAILS the task BEFORE
    /// the agent is prompted — never a normal no-change completion, and no reset is ever
    /// issued from stale evidence after the failed fetch.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_FailedPreparationFetch_FailsBeforePrompting()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args.StartsWith("fetch ", StringComparison.Ordinal)
                ? (128, "", "fatal: authentication failed")
                : null,
        };
        var agentRunner = new MockAgentRunner();

        var (result, _, stderr) = await RunImproverLegacyAsync(
            "improver-legacy-prep-fail", configRepoDir, git, agentRunner: agentRunner);

        // The preflight plus the failed fetch — nothing after it, so no FETCH_HEAD fallback,
        // no reset, no clean and no publication flow ever launched.
        Assert.Equal(
            [
                "rev-parse --show-toplevel",
                "rev-parse --verify HEAD^{commit}",
                "rev-parse --symbolic-full-name HEAD",
                "rev-parse --symbolic-full-name @{upstream}",
                $"fetch origin \"{ConfigRepoPreparationFakes.BranchRef}\"",
            ],
            git.GitCommands);

        // The agent was never prompted.
        Assert.Empty(agentRunner.PromptCalls);

        // Truthful failure: Failed outcome, authoritative FAIL verdict, sanitized reason.
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("Config repo preparation fetch failed (exit 128)") && i.Contains("authentication failed"));
        Assert.Contains(
            "Config repo preparation fetch failed (exit 128)",
            FindLine(stderr, "Config repo preparation fetch failed"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// TRUTHFUL PREPARATION: a preparation FETCH that the SEAM cannot even launch is a truthful
    /// failure — the seam maps an unlaunchable transport command to its exit -1 rejection with
    /// a fixed <c>SanitizedError</c>, the task FAILS before the agent is prompted, and no raw
    /// exception text escapes.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_ThrownPreparationFetch_IsSanitizedFailure()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens is ["fetch", ..]
                ? throw new InvalidOperationException("git binary exploded")
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-prep-throw", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        Assert.Empty(agentRunner.PromptCalls);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        // The seam's rejection is the authoritative sanitized classification — no message text.
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("Config repo preparation fetch failed (exit -1)") && !i.Contains("exploded"));
        // No fallback baseline, no destructive restore and no publication after the failure.
        Assert.DoesNotContain(fake.Launched, t => t is ["rev-parse", "--verify", "FETCH_HEAD^{commit}"]);
        Assert.DoesNotContain(fake.Launched, t => t is ["reset", ..]);
        Assert.DoesNotContain(fake.Launched, t => t is ["clean", ..]);
        Assert.DoesNotContain(fake.Launched, t => t is ["add", ..]);
    }

    /// <summary>
    /// A SUCCESSFUL empty staged diff is a genuine NO-CHANGE completion — Completed, PASS,
    /// Pushed=false, and clearly distinct from every failure.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_EmptyStagedDiff_IsNoChangeSuccess()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args == "diff --cached --name-only -z"
                ? (0, "", "") // successful EMPTY diff — not a failure
                : null,
        };
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-empty-diff", configRepoDir, git, agentRunner: agentRunner);

        // The commit was never reached, and no failure was manufactured.
        Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("commit -m"));
        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Equal("PASS", result.Metrics!.Verdict);
        Assert.Empty(result.Metrics.Issues);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(0, result.GitStatus.FilesChanged);
        Assert.DoesNotContain("[Config Repo Git Failure]", result.Output);
    }

    /// <summary>
    /// A FAILED diff (non-zero exit) is distinct from a successful empty diff: publication
    /// stops — no commit ever launches — and the task fails truthfully.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_FailedDiff_StopsPublicationAndFails()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args == "diff --cached --name-only -z"
                ? (1, "", "fatal: diff failed")
                : null,
        };
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-diff-fail", configRepoDir, git, agentRunner: agentRunner);

        // EVERY subsequent forbidden command is absent — publication stopped at the failed diff.
        AssertLegacyStoppedAfterDiff(git.GitCommands);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("git diff failed (exit 1)") && i.Contains("fatal: diff failed"));
        // The agent's output evidence is preserved verbatim with the reason appended.
        Assert.StartsWith("Mock agent response", result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Git Failure]", result.Output);
    }

    /// <summary>
    /// A THROWN post-commit-pull (ordinary exception) is a publication failure with the
    /// sanitized classification, and push NEVER launches.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_ThrownPostCommitPull_NeverPushes()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                "diff --cached --name-only -z" => (0, StagedOutput("agents/coder.agents.md"), ""),
                _ => null,
            },
            GitCommandThrower = args => args == "pull --no-rebase"
                ? new InvalidOperationException("pull exploded")
                : null,
        };
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-pull-throw", configRepoDir, git, agentRunner: agentRunner);

        // push (and merge --abort, which belongs to the non-zero-pull path) never launched.
        Assert.DoesNotContain(git.GitCommands, c => c == "push");
        Assert.DoesNotContain(git.GitCommands, c => c == "merge --abort");
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("git pull failed with an error [InvalidOperationException]") && !i.Contains("exploded"));
        // Evidence preserved verbatim + sanitized reason appended.
        Assert.StartsWith("Mock agent response", result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Git Failure]", result.Output);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(["agents/coder.agents.md"], result.GitStatus.ChangedFiles);
    }

    /// <summary>
    /// OCE without a requested cancellation from a config Git command is a FAILED outcome
    /// (FAIL verdict) — never a normal completion and never Cancelled.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_NonCancellationOCE_YieldsFailed()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        using var unrelatedCts = new CancellationTokenSource();
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens is ["fetch", ..]
                ? throw new OperationCanceledException("simulated timeout", unrelatedCts.Token)
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        // The execution token is NOT cancelled — the OCE is an ordinary failure.
        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-oce-fail", configRepoDir, seam, fake, git,
            TestContext.Current.CancellationToken, agentRunner);

        Assert.Empty(agentRunner.PromptCalls);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("Config repo preparation fetch was interrupted without a requested cancellation [OperationCanceledException]"));
    }

    /// <summary>
    /// A REQUESTED execution cancellation during a config Git command keeps its established
    /// semantics: Cancelled outcome with a CANCELLED verdict (via the ct guard), not a
    /// publication failure.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_RequestedCancellationDuringConfigGit_YieldsCancelled()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        using var cts = new CancellationTokenSource();
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens =>
            {
                if (tokens is ["fetch", ..])
                {
                    cts.Cancel();
                    throw new OperationCanceledException("shutdown", cts.Token);
                }
                return null;
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-oce-cancelled", configRepoDir, seam, fake, git, cts.Token);

        Assert.Equal(TaskOutcome.Cancelled, result.Status);
        Assert.Equal("CANCELLED", result.Metrics!.Verdict);
        // The OCE was NOT reclassified as a publication failure.
        Assert.DoesNotContain(result.Metrics.Issues, i => i.Contains("Config repo preparation"));
    }

    /// <summary>
    /// REPORT PRECEDENCE (worker report): an ordinary Improver Git failure yields FAIL even
    /// when the agent filed a passing WORKER report whose verdict would otherwise dominate.
    /// The selected <see cref="TaskMetrics.Summary"/> must expose the Git failure — never
    /// only the passing report's summary — and the accumulated agent output evidence must
    /// be retained verbatim with the sanitized reason appended.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_PushFailure_FailsDespitePassingReports()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = ConfigRepoResponder(["agents/coder.agents.md"], pushFails: true),
        };
        var agentRunner = new MockAgentRunner
        {
            WorkerReportToReturn = new WorkerReport
            {
                TaskVerdict = TaskVerdict.Pass,
                Summary = "Improver summary",
                Issues = [],
            },
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-precedence", configRepoDir, git, agentRunner: agentRunner);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Contains(result.Metrics.Issues, i => i.Contains("git push failed"));
        // The Git failure is exposed through the selected metrics — a passing report's summary
        // must NEVER be selected on a Git failure (on the failure path the catch constructs
        // fresh FAIL metrics, so the report summary cannot hide the failure here).
        Assert.DoesNotContain("Improver summary", result.Metrics.Summary, StringComparison.Ordinal);

        // The worker report's summary survives in the returned output evidence (evidence is
        // not replaced), but the verdict and the Git failure issue are authoritative.
        Assert.StartsWith("Mock agent response", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// REPORT PRECEDENCE (test report): an ordinary Improver Git failure yields FAIL even
    /// when the agent filed a PASSING TESTER report — the test report source would otherwise
    /// dominate the metrics construction. The Git failure must appear in the issues AND be
    /// exposed by the selected <see cref="TaskMetrics.Summary"/>.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_PushFailure_FailsDespitePassingTestReport()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = ConfigRepoResponder(["agents/coder.agents.md"], pushFails: true),
        };
        var agentRunner = new MockAgentRunner
        {
            // A PASSING tester report — the highest-precedence metrics source.
            TestReportToReturn = new TestResultReport
            {
                Verdict = TaskVerdict.Pass,
                TotalTests = 5,
                PassedTests = 5,
                FailedTests = 0,
                Summary = "All 5 tests passed, build succeeded",
            },
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-precedence-testreport", configRepoDir, git, agentRunner: agentRunner);

        // The Git failure dominates BOTH report sources: verdict FAIL and a Git failure issue.
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Contains(result.Metrics.Issues, i => i.Contains("git push failed"));
        // The passing test report's own summary is never selected on a Git failure — the
        // failure path constructs fresh FAIL metrics, so the passing summary cannot hide it.
        Assert.DoesNotContain("All 5 tests passed", result.Metrics.Summary, StringComparison.Ordinal);
        Assert.StartsWith("Mock agent response", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Confirmed successful publication: Completed outcome, PASS verdict, Pushed=true. The
    /// no-change and failure outcomes are all distinct.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_ConfirmedPush_IsSuccessfulCompletion()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        string[] staged = ["agents/coder.agents.md"];
        var git = new MockGitOperations
        {
            GitCommandResponder = ConfigRepoResponder(staged, pushFails: false),
        };
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-push-ok", configRepoDir, git, agentRunner: agentRunner);

        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Equal("PASS", result.Metrics!.Verdict);
        Assert.True(result.GitStatus!.Pushed);
        Assert.Equal(1, result.GitStatus.FilesChanged);
        Assert.Equal(staged, result.GitStatus.ChangedFiles);
        Assert.DoesNotContain("[Config Repo Git Failure]", result.Output);
    }

    /// <summary>
    /// LEGACY path, preparation FETCH THROWN as an ordinary exception: truthful failure with a
    /// sanitized classification, before prompting and before any destructive restore.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_ThrownPreparationFetch_IsSanitizedFailure()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandThrower = args => args.StartsWith("fetch ", StringComparison.Ordinal)
                ? new InvalidOperationException("git binary missing")
                : null,
        };
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-prep-throw", configRepoDir, git, agentRunner: agentRunner);

        Assert.Empty(agentRunner.PromptCalls);
        Assert.Equal(
            [
                "rev-parse --show-toplevel",
                "rev-parse --verify HEAD^{commit}",
                "rev-parse --symbolic-full-name HEAD",
                "rev-parse --symbolic-full-name @{upstream}",
                $"fetch origin \"{ConfigRepoPreparationFakes.BranchRef}\"",
            ],
            git.GitCommands);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("Config repo preparation fetch failed with an error [InvalidOperationException]")
                && !i.Contains("git binary missing"));
    }

    /// <summary>
    /// SEAM path, non-zero git ADD: publication stops before the diff, the task fails
    /// truthfully, and no diagnostic paths are manufactured.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_FailedAdd_StopsPublicationAndFails()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens is ["add", ..]
                ? new GitProcessResult(1, "", "fatal: bad path")
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-add-fail", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        // EVERY subsequent forbidden command is absent — publication stopped at the failed add.
        AssertSeamStoppedAfterAdd(fake.Launched);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(0, result.GitStatus.FilesChanged);
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("git add failed (exit 1)") && i.Contains("fatal: bad path"));
        // Evidence preserved verbatim + sanitized reason appended.
        Assert.StartsWith("Mock agent response", result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Git Failure]", result.Output);
    }

    /// <summary>
    /// SEAM path, THROWN commit (ordinary exception): publication stops — no pull, no push —
    /// and the staged diagnostics still reach the result.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_ThrownCommit_StopsPublicationAndFails()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens switch
            {
                ["diff", ..] => new GitProcessResult(0, StagedOutput("agents/coder.agents.md"), ""),
                ["commit", ..] => throw new InvalidOperationException("commit exploded"),
                _ => null,
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-commit-throw", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        // EVERY subsequent forbidden command is absent — publication stopped at the commit.
        AssertSeamStoppedAfterCommit(fake.Launched);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(["agents/coder.agents.md"], result.GitStatus.ChangedFiles);
        // The seam maps an unlaunchable command to its exit -1 rejection with a FIXED
        // SanitizedError — no raw exception text escapes.
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("git commit failed (exit -1)") && !i.Contains("exploded"));
    }

    /// <summary>
    /// SEAM path, non-zero PUSH: the publication failure is truthful — Failed outcome, FAIL
    /// verdict, Pushed never claimed, and no retry or second push attempt.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_FailedPush_IsPublicationFailure()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var pushCalls = 0;
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens switch
            {
                ["diff", ..] => new GitProcessResult(0, StagedOutput("agents/coder.agents.md"), ""),
                ["push", "origin", "HEAD"] => new GitProcessResult(1, "", "remote rejected: permission denied"),
                _ => null,
            },
        };
        pushCalls = fake.Launched.Count(t => t is ["push", ..]);
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-push-fail", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(["agents/coder.agents.md"], result.GitStatus.ChangedFiles);
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("git push failed (exit 1)") && i.Contains("remote rejected: permission denied"));
        // Exactly ONE push attempt — no force push, retry, or remote rollback.
        Assert.Single(fake.Launched, t => t is ["push", ..]);
        Assert.StartsWith("Mock agent response", result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Git Failure]", result.Output);
    }

    // ── (g) Stage/path/failure-form matrix: the missing converse cells ────────
    //
    // Every named publication stage must be protected on BOTH command-routing paths
    // (injected seam and legacy) and BOTH failure forms (nonzero exit and ordinary
    // exception). Each test below proves the converse cell of an existing case, and each
    // asserts the FULL forbidden subsequence absence so removing the protection the test
    // names makes it fail.

    /// <summary>
    /// LEGACY path, non-zero git ADD (converse of the seam nonzero case): publication stops
    /// before the diff and EVERY subsequent forbidden command is absent.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_FailedAdd_StopsPublicationAndFails()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args == "add agents/*.agents.md"
                ? (1, "", "fatal: bad path")
                : null,
        };
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-add-fail", configRepoDir, git, agentRunner: agentRunner);

        // No diff, no commit, no post-commit pull, no merge --abort, no push.
        AssertLegacyStoppedAfterAdd(git.GitCommands);
        Assert.Equal(
            [
                .. ConfigRepoPreparationFakes.LegacyCommands,
                "add agents/*.agents.md",
                // The step-end cleanup: no confirmed publication after the failed add, so the
                // restore target is the captured fetched baseline.
                .. ConfigRepoPreparationFakes.LegacyCleanupCommands,
            ],
            git.GitCommands);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(0, result.GitStatus.FilesChanged);
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("git add failed (exit 1)") && i.Contains("fatal: bad path"));
        // Evidence preserved verbatim + sanitized reason appended.
        Assert.StartsWith("Mock agent response", result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Git Failure]", result.Output);
    }

    /// <summary>
    /// SEAM path, non-zero git DIFF (converse of the legacy nonzero case): publication stops
    /// before the commit and EVERY subsequent forbidden command is absent.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_FailedDiff_StopsPublicationAndFails()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens is ["diff", ..]
                ? new GitProcessResult(1, "", "fatal: diff failed")
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-diff-fail", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        // No commit, no post-commit pull, no merge --abort, no push.
        AssertSeamStoppedAfterDiff(fake.Launched);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(0, result.GitStatus.FilesChanged);
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("git diff failed (exit 1)") && i.Contains("fatal: diff failed"));
        Assert.StartsWith("Mock agent response", result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Git Failure]", result.Output);
    }

    /// <summary>
    /// SEAM path, non-zero git COMMIT (converse of the legacy nonzero case): publication
    /// stops — no post-commit pull, no merge --abort, no push — and the staged diagnostics
    /// still reach the result.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_FailedCommit_StopsPublicationAndFails()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens switch
            {
                ["diff", ..] => new GitProcessResult(0, StagedOutput("agents/coder.agents.md"), ""),
                ["commit", ..] => new GitProcessResult(1, "", "nothing to commit / hook rejected"),
                _ => null,
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-commit-fail", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        // No post-commit pull, no merge --abort, no push.
        AssertSeamStoppedAfterCommit(fake.Launched);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(["agents/coder.agents.md"], result.GitStatus.ChangedFiles);
        Assert.Equal(1, result.GitStatus.FilesChanged);
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("git commit failed (exit 1)") && i.Contains("hook rejected"));
        Assert.StartsWith("Mock agent response", result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Git Failure]", result.Output);
    }

    /// <summary>
    /// LEGACY path, THROWN git COMMIT — ordinary exception, not a non-zero exit (converse of
    /// the seam thrown case): publication stops and EVERY subsequent forbidden command is
    /// absent. The sanitized classification replaces the raw message; the staged diagnostics
    /// still reach the result.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_ThrownCommit_StopsPublicationAndFails()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args == "diff --cached --name-only -z"
                ? (0, StagedOutput("agents/coder.agents.md"), "")
                : null,
            GitCommandThrower = args => args.StartsWith("commit -m")
                ? new InvalidOperationException("commit exploded")
                : null,
        };
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-commit-throw", configRepoDir, git, agentRunner: agentRunner);

        // No post-commit pull, no merge --abort, no push.
        AssertLegacyStoppedAfterCommit(git.GitCommands);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(["agents/coder.agents.md"], result.GitStatus.ChangedFiles);
        // SANITIZED: type-name classification only — no raw message text escapes.
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("git commit failed with an error [InvalidOperationException]")
                && !i.Contains("commit exploded"));
        Assert.StartsWith("Mock agent response", result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Git Failure]", result.Output);
    }

    /// <summary>
    /// SEAM path, THROWN post-commit pull — ordinary exception, not a non-zero exit (converse
    /// of the legacy thrown case): no merge --abort, no push, staged diagnostics preserved.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_ThrownPostCommitPull_NeverPushes()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens switch
            {
                ["diff", ..] => new GitProcessResult(0, StagedOutput("agents/coder.agents.md"), ""),
                ["pull", "--no-rebase", ..] => throw new InvalidOperationException("pull exploded"),
                _ => null,
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-pull-throw", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        // The seam maps an unlaunchable command to its exit -1 rejection — so the merge-abort
        // attempt from the non-zero-pull path does not fire here either; push NEVER launches.
        Assert.DoesNotContain(fake.Launched, t => t is ["push", ..]);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(["agents/coder.agents.md"], result.GitStatus.ChangedFiles);
        // SANITIZED: the seam's fixed rejection classification — no raw message text escapes.
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("git pull failed (exit -1)") && !i.Contains("pull exploded"));
        Assert.StartsWith("Mock agent response", result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Git Failure]", result.Output);
    }

    /// <summary>
    /// SEAM path, THROWN push — ordinary exception, not a non-zero exit (converse of the seam
    /// nonzero push case): a publication failure whose sanitized classification never carries
    /// the raw message, with exactly ONE push attempt and no retry.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_ThrownPush_IsPublicationFailure()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens switch
            {
                ["diff", ..] => new GitProcessResult(0, StagedOutput("agents/coder.agents.md"), ""),
                ["push", "origin", "HEAD"] => throw new InvalidOperationException("push exploded"),
                _ => null,
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-push-throw", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(["agents/coder.agents.md"], result.GitStatus.ChangedFiles);
        // SANITIZED: the seam's fixed rejection classification — no raw message text escapes.
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains("git push failed (exit -1)") && !i.Contains("push exploded"));
        // Exactly ONE push attempt — no force push, retry, or remote rollback.
        Assert.Single(fake.Launched, t => t is ["push", ..]);
        Assert.StartsWith("Mock agent response", result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Git Failure]", result.Output);
    }

    /// <summary>
    /// FULL EVIDENCE: the complete accumulated MULTI-PART agent/retry output (initial response
    /// plus the size-enforcement retry segment) is retained VERBATIM on an ordinary publication
    /// failure, with the sanitized stage reason APPENDED — never replacing or truncating any
    /// part of the evidence. The size-enforcement retry genuinely repairs the oversized file,
    /// so the flow reaches the push and fails there.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_PushFailure_PreservesCompleteMultiPartEvidence()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var filePath = Path.Combine(configRepoDir, "agents", "coder.agents.md");

        const string initialSegment = "Initial improver analysis: reviewed every guidance file.";
        const string retrySegment = "Condensation retry: compressed the older material from the top.";
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = async (prompt, _, ct) =>
            {
                if (prompt.Contains("append-new/compress-old policy", StringComparison.Ordinal))
                {
                    // The genuine repair the enforcement retry asks for — one retry succeeds.
                    await File.WriteAllTextAsync(filePath, "repaired", ct);
                    return retrySegment;
                }

                // The oversized file is the IMPROVER'S OWN edit, written from inside the agent
                // callback — i.e. AFTER the pre-run baseline preparation, whose `clean -fdx`
                // deliberately clears untracked residue. This reproduces exactly the state the
                // fixture used to pre-seed, at the point the improver would really produce it,
                // so the size-enforcement retry still fires and the multi-part evidence
                // contract is exercised unchanged.
                await File.WriteAllTextAsync(
                    filePath, new string('x', WorkerConstants.AgentsMdMaxCharacters + 1), ct);
                Assert.Equal(
                    WorkerConstants.AgentsMdMaxCharacters + 1,
                    (await File.ReadAllTextAsync(filePath, ct)).Length);
                return initialSegment;
            },
        };
        var git = new MockGitOperations
        {
            GitCommandResponder = ConfigRepoResponder(["agents/coder.agents.md"], pushFails: true),
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-evidence", configRepoDir, git, agentRunner: agentRunner);

        // Two prompts were delivered (the initial one plus the size-enforcement retry), and
        // BOTH segments are retained verbatim in the returned evidence.
        Assert.Equal(2, agentRunner.PromptCalls.Count);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.StartsWith(initialSegment, result.Output, StringComparison.Ordinal);
        Assert.Contains(retrySegment, result.Output, StringComparison.Ordinal);
        Assert.Contains("[Agents.md size enforcement]", result.Output, StringComparison.Ordinal);
        // The sanitized reason is APPENDED after the evidence — never replacing it.
        var reasonIndex = result.Output.IndexOf("[Config Repo Git Failure]", StringComparison.Ordinal);
        Assert.True(reasonIndex > result.Output.IndexOf(retrySegment, StringComparison.Ordinal));
        Assert.Contains("git push failed (exit 1): remote rejected: permission denied", result.Output, StringComparison.Ordinal);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(["agents/coder.agents.md"], result.GitStatus.ChangedFiles);
    }

    // ── (h) The pre-run baseline PREPARATION sequence ─────────────────────────
    //
    // The Improver's preparation replaced the old pull-only step with a verified,
    // freshly-fetched remote baseline: preflight → fetch → resolve → destructive restore →
    // verify. Every rejection below happens BEFORE the agent is prompted and BEFORE any
    // mutation that the rejection is meant to prevent.

    /// <summary>A second, DIFFERENT full 40-hex SHA — the remote baseline in mixed-SHA tests.</summary>
    private const string RemoteBaselineSha = "2222222222222222222222222222222222222222";

    /// <summary>
    /// SEAM happy path: the preparation issues the EXACT sequence in order — the four preflight
    /// rev-parse forms, the fetch of the discovered branch ref, the FETCH_HEAD resolution, the
    /// destructive reset/clean, and the post-restore HEAD + verbose-status verification —
    /// BEFORE the agent is prompted.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_Preparation_RunsTheExactSequenceBeforePrompting()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        List<string[]> launchedBeforePrompt = [];
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens[0] == "diff" ? new GitProcessResult(0, "", "") : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) =>
            {
                launchedBeforePrompt = [.. fake.Launched];
                return Task.FromResult("Mock agent response");
            },
        };

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-prep-ok", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        Assert.Equal(TaskOutcome.Completed, result.Status);

        // The COMPLETE preparation sequence had already run when the agent was prompted.
        Assert.Equal(ConfigRepoPreparationFakes.SeamLaunches.Length, launchedBeforePrompt.Count);
        for (var i = 0; i < ConfigRepoPreparationFakes.SeamLaunches.Length; i++)
            Assert.Equal(ConfigRepoPreparationFakes.SeamLaunches[i], launchedBeforePrompt[i]);

        // The old pull-only preparation is GONE: no `pull --ff-only` on any path.
        Assert.DoesNotContain(fake.Launched, t => t is ["pull", "--ff-only", ..]);
        Assert.Empty(git.GitCommands);
    }

    /// <summary>
    /// LEGACY happy path: the SAME sequence arrives as the exact opaque argument strings, with
    /// the DISCOVERED branch ref carried as ONE quoted argument (never interpolated into a
    /// shell-sensitive position) and the reset target spelled as the captured full SHA.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_Preparation_SendsTheExactOpaqueStrings()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args == "diff --cached --name-only -z"
                ? (0, "", "")
                : null,
        };

        await RunImproverLegacyAsync("improver-legacy-prep-ok", configRepoDir, git);

        Assert.Equal(
            [
                .. ConfigRepoPreparationFakes.LegacyCommands,
                "add agents/*.agents.md",
                "diff --cached --name-only -z",
                // The step-end cleanup: the empty staged diff means no confirmed publication,
                // so the restore target is the captured fetched baseline.
                .. ConfigRepoPreparationFakes.LegacyCleanupCommands,
            ],
            git.GitCommands);
        Assert.Contains(
            $"fetch origin \"{ConfigRepoPreparationFakes.BranchRef}\"", git.GitCommands);
        Assert.DoesNotContain("pull --ff-only", git.GitCommands);
        Assert.All(git.WorkDirs, dir => Assert.Equal(configRepoDir, dir));
    }

    /// <summary>
    /// THE POINT OF THE FETCH-FIRST SEQUENCE: the reset target is the SHA resolved from THIS
    /// run's <c>FETCH_HEAD</c> — never the local HEAD. A prior failed-push commit sitting on the
    /// local branch (a clean-looking checkout!) is therefore discarded, not carried forward.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_Preparation_ResetsToTheFetchedShaNotTheLocalHead()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        var headCalls = 0;
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens switch
            {
                // The PRE-fetch HEAD is a prior failed-push commit; the POST-restore HEAD is the
                // fetched baseline.
                ["rev-parse", "--verify", "HEAD^{commit}"] =>
                    new GitProcessResult(0, (++headCalls == 1 ? LocalAheadSha : RemoteBaselineSha) + "\n", ""),
                ["rev-parse", "--verify", "FETCH_HEAD^{commit}"] =>
                    new GitProcessResult(0, RemoteBaselineSha + "\n", ""),
                ["diff", ..] => new GitProcessResult(0, "", ""),
                _ => null,
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-prep-fetched-sha", configRepoDir, seam, fake, git);

        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Contains(fake.Launched, t => t is ["reset", "--hard", RemoteBaselineSha]);
        // The local (prior failed-push) commit was NEVER a reset target.
        Assert.DoesNotContain(fake.Launched, t => t is ["reset", "--hard", LocalAheadSha]);
    }

    /// <summary>A local-only commit SHA that must never become a reset target.</summary>
    private const string LocalAheadSha = "3333333333333333333333333333333333333333";

    /// <summary>
    /// A FOREIGN worktree root (<c>rev-parse --show-toplevel</c> reporting a different
    /// repository) is rejected BEFORE any mutation: no fetch, no reset, no clean, and the agent
    /// is never prompted.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_Preparation_ForeignRoot_IsRejectedBeforeAnyMutation()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens is ["rev-parse", "--show-toplevel"]
                ? new GitProcessResult(0, Path.Combine(Path.GetTempPath(), "some-other-repo") + "\n", "")
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-foreign-root", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        AssertPreparationRejected(result, agentRunner, "the worktree root is not the configured config repository");
        AssertNoMutationLaunched(fake);
        // The top-level probe was the ONLY launch — no further preflight command ran.
        Assert.Single(fake.Launched, t => t is ["rev-parse", "--show-toplevel"]);
        Assert.Single(fake.Launched);
    }

    /// <summary>
    /// A DETACHED HEAD (<c>rev-parse --symbolic-full-name HEAD</c> reporting the bare
    /// <c>HEAD</c>) is a topology rejection — no branch is ever guessed.
    /// </summary>
    [Theory]
    [InlineData("HEAD")]
    [InlineData("refs/remotes/origin/main")]
    [InlineData("refs/tags/v1")]
    public async Task Improver_SeamPath_Preparation_DetachedHead_IsRejected(string symbolicName)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens is ["rev-parse", "--symbolic-full-name", "HEAD"]
                ? new GitProcessResult(0, symbolicName + "\n", "")
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-detached-" + symbolicName.Length, configRepoDir, seam, fake, git,
            agentRunner: agentRunner);

        AssertPreparationRejected(result, agentRunner, "HEAD is not attached to a usable branch");
        AssertNoMutationLaunched(fake);
    }

    /// <summary>
    /// A MISSING upstream (a non-zero <c>@{upstream}</c>), a DIFFERENT remote, and a MISMATCHED
    /// branch name are all topology rejections: the ordinary single-origin clone is the only
    /// supported topology, and no fetch or restore is attempted.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("refs/remotes/upstream/main")]
    [InlineData("refs/remotes/origin/other")]
    [InlineData("refs/heads/main")]
    public async Task Improver_SeamPath_Preparation_UpstreamProblem_IsRejected(string? upstream)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens is ["rev-parse", "--symbolic-full-name", "@{upstream}"]
                ? (upstream is null
                    ? new GitProcessResult(128, "", "fatal: no upstream configured")
                    : new GitProcessResult(0, upstream + "\n", ""))
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-upstream-" + (upstream?.Length ?? 0), configRepoDir, seam, fake, git,
            agentRunner: agentRunner);

        var expected = upstream is null
            ? "Config repo preparation upstream check failed (exit 128)"
            : "the branch has no matching origin upstream";
        AssertPreparationRejected(result, agentRunner, expected);
        AssertNoMutationLaunched(fake);
    }

    /// <summary>
    /// MALFORMED rev-parse SHA output — blank, whitespace-only, ambiguous (two lines),
    /// abbreviated, or non-hex — is rejected for BOTH the preflight HEAD probe and the fetched
    /// baseline probe. An exit-zero command with unusable output must never yield a baseline.
    /// </summary>
    [Theory]
    [InlineData(true, "")]
    [InlineData(true, "   \n")]
    [InlineData(true, "1111111\n")]
    [InlineData(true, "not-a-sha-at-all-not-a-sha-at-all-not-hex\n")]
    [InlineData(true, "1111111111111111111111111111111111111111\n2222222222222222222222222222222222222222\n")]
    [InlineData(false, "")]
    [InlineData(false, "1111111\n")]
    [InlineData(false, "1111111111111111111111111111111111111111\n2222222222222222222222222222222222222222\n")]
    public async Task Improver_SeamPath_Preparation_MalformedShaOutput_IsRejected(
        bool preflightHead, string stdout)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var target = preflightHead ? "HEAD^{commit}" : "FETCH_HEAD^{commit}";
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens =>
                tokens.Count == 3 && tokens[0] == "rev-parse" && tokens[1] == "--verify" && tokens[2] == target
                    ? new GitProcessResult(0, stdout, "")
                    : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            $"improver-seam-badsha-{preflightHead}-{stdout.Length}", configRepoDir, seam, fake, git,
            agentRunner: agentRunner);

        var label = preflightHead ? "HEAD" : "FETCH_HEAD";
        AssertPreparationRejected(
            result, agentRunner, $"{label} did not resolve to a single full commit SHA");

        if (preflightHead)
        {
            // The malformed PREFLIGHT HEAD stops everything before any mutation.
            AssertNoMutationLaunched(fake);
        }
        else
        {
            // The malformed FETCHED baseline stops the restore — no reset from stale evidence.
            Assert.DoesNotContain(fake.Launched, t => t is ["reset", ..]);
            Assert.DoesNotContain(fake.Launched, t => t is ["clean", ..]);
        }
    }

    /// <summary>
    /// After the restore, a HEAD that does NOT equal the captured baseline SHA is a truthful
    /// preparation failure — the agent is never prompted on an unverified checkout.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_Preparation_HeadMismatchAfterRestore_IsRejected()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var headCalls = 0;
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens is ["rev-parse", "--verify", "HEAD^{commit}"]
                // The POST-restore HEAD disagrees with the fetched baseline.
                ? new GitProcessResult(
                    0,
                    (++headCalls == 1 ? ConfigRepoPreparationFakes.BaselineSha : LocalAheadSha) + "\n",
                    "")
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-head-mismatch", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        AssertPreparationRejected(
            result, agentRunner, "HEAD does not match the fetched baseline after the restore");
        // No second forced clean during PREPARATION and no publication after the mismatch.
        // (The step-end cleanup's own clean is a separate, later, verified sequence.)
        Assert.Equal(2, fake.Launched.Count(t => t is ["clean", ..]));
        Assert.DoesNotContain(fake.Launched, t => t is ["add", ..]);
    }

    /// <summary>
    /// RESIDUAL working-tree content after the restore (e.g. a protected nested repository the
    /// single-force clean cannot remove) is reported TRUTHFULLY: no second forced clean, no
    /// recursive deletion, no quarantine flag — and the agent is never prompted.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_Preparation_NonEmptyStatus_IsRejectedWithoutEscalation()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens is ["status", "--porcelain=v1", ..]
                ? new GitProcessResult(0, "?? nested/\n", "")
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-dirty-status", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        AssertPreparationRejected(
            result, agentRunner, "the working tree is not clean after the restore");
        // NO ESCALATION: exactly one PREPARATION clean and one PREPARATION status, no
        // publication. (The step-end cleanup's own clean + status are a separate, later,
        // verified sequence.)
        Assert.Equal(2, fake.Launched.Count(t => t is ["clean", ..]));
        Assert.Equal(2, fake.Launched.Count(t => t is ["status", ..]));
        Assert.DoesNotContain(fake.Launched, t => t is ["add", ..]);
    }

    /// <summary>
    /// LEGACY path converse of the foreign-root rejection: the same lexical trust boundary
    /// applies when the commands are routed as opaque argument strings.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_Preparation_ForeignRoot_IsRejectedBeforeAnyMutation()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args == "rev-parse --show-toplevel"
                ? (0, Path.Combine(Path.GetTempPath(), "some-other-repo") + "\n", "")
                : null,
        };
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-foreign-root", configRepoDir, git, agentRunner: agentRunner);

        AssertPreparationRejected(
            result, agentRunner, "the worktree root is not the configured config repository");
        Assert.Equal(["rev-parse --show-toplevel"], git.GitCommands);
    }

    /// <summary>
    /// LEGACY path converse of the residual-status rejection.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_Preparation_NonEmptyStatus_IsRejected()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args =>
                args == "status --porcelain=v1 --untracked-files=all --ignored"
                    ? (0, "?? nested/\n", "")
                    : null,
        };
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-dirty-status", configRepoDir, git, agentRunner: agentRunner);

        AssertPreparationRejected(
            result, agentRunner, "the working tree is not clean after the restore");
        // The PREPARATION sequence plus the step-end cleanup (whose restore target is the
        // captured fetched baseline, and whose status oracle is exercised verbatim here).
        Assert.Equal(
            [
                .. ConfigRepoPreparationFakes.LegacyCommands,
                .. ConfigRepoPreparationFakes.LegacyCleanupCommands,
            ],
            git.GitCommands);
        Assert.DoesNotContain("add agents/*.agents.md", git.GitCommands);
    }

    /// <summary>
    /// A preparation rejection is a truthful Failed/FAIL outcome whose sanitized reason carries
    /// the expected stage wording, and the agent was NEVER prompted.
    /// </summary>
    private static void AssertPreparationRejected(
        TaskResult result, MockAgentRunner agentRunner, string expectedReasonFragment)
    {
        Assert.Empty(agentRunner.PromptCalls);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Contains(result.Metrics.Issues,
            i => i.Contains(expectedReasonFragment, StringComparison.Ordinal));
    }

    /// <summary>
    /// A preflight rejection precedes EVERY mutation: no fetch, no reset, no clean, and no
    /// publication command was ever launched.
    /// </summary>
    private static void AssertNoMutationLaunched(SeamProcessRunnerFake fake)
    {
        Assert.DoesNotContain(fake.Launched, t => t is ["fetch", ..]);
        Assert.DoesNotContain(fake.Launched, t => t is ["reset", ..]);
        Assert.DoesNotContain(fake.Launched, t => t is ["clean", ..]);
        Assert.DoesNotContain(fake.Launched, t => t is ["checkout", ..]);
        Assert.DoesNotContain(fake.Launched, t => t is ["add", ..]);
        Assert.DoesNotContain(fake.Launched, t => t is ["commit", ..]);
        Assert.DoesNotContain(fake.Launched, t => t is ["push", ..]);
    }

    // ── (i) Preparation sequence: exhaustive failure vectors (round 2) ────────
    //
    // Every vector below asserts the SAME three invariants beyond its own subject:
    //   * the agent runner was NEVER invoked (zero prompts),
    //   * no publication command ever ran (no add/diff/commit/pull/push),
    //   * the outcome is the sanitized ConfigRepoPublicationException preparation
    //     failure (Failed + FAIL) carrying no raw stderr and no exception message.

    /// <summary>
    /// THE DISTINCTION THAT MATTERS: the preflight peels HEAD to a COMMIT
    /// (<c>rev-parse --verify HEAD^{commit}</c>). A checkout whose bare <c>HEAD</c> resolves
    /// fine (e.g. it names a tag or another non-commit object) but whose peeled commit form
    /// FAILS must be rejected — proving the bare form is not, and never was, a substitute.
    /// The fake answers the bare form with a valid SHA to make the point unambiguous.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_Preparation_BareHeadResolvesButPeeledCommitFails_IsRejected()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens switch
            {
                // The BARE form would happily succeed — a tag object satisfies it.
                ["rev-parse", "--verify", "HEAD"] =>
                    new GitProcessResult(0, ConfigRepoPreparationFakes.BaselineSha + "\n", ""),
                // The PEELED-COMMIT form is what production actually issues, and it fails.
                ["rev-parse", "--verify", "HEAD^{commit}"] =>
                    new GitProcessResult(128, "", "fatal: Needed a single revision"),
                _ => null,
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-bare-head-only", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        AssertPreparationRejected(
            result, agentRunner, "Config repo preparation HEAD commit check failed (exit 128)");
        AssertNoMutationLaunched(fake);
        AssertNoPublicationLaunched(fake);

        // Production never falls back to the bare form: it was never even issued.
        Assert.DoesNotContain(fake.Launched, t => t is ["rev-parse", "--verify", "HEAD"]);
        Assert.Contains(fake.Launched, t => t is ["rev-parse", "--verify", "HEAD^{commit}"]);
    }

    /// <summary>
    /// An UNBORN repository (a fresh clone/init with no commit) fails
    /// <c>rev-parse --verify HEAD^{commit}</c>. It is rejected before any mutation — never
    /// "prepared" into an empty baseline.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_Preparation_UnbornRepository_IsRejected()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens is ["rev-parse", "--verify", "HEAD^{commit}"]
                ? new GitProcessResult(128, "", "fatal: ambiguous argument 'HEAD': unknown revision")
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, stderr) = await RunImproverWithSeamAsync(
            "improver-seam-unborn", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        AssertPreparationRejected(
            result, agentRunner, "Config repo preparation HEAD commit check failed (exit 128)");
        AssertNoMutationLaunched(fake);
        AssertNoPublicationLaunched(fake);
        // The sanitized stderr is rendered through RenderForLog — one line, no forged lines.
        Assert.Contains(
            "Config repo preparation HEAD commit check failed (exit 128)",
            FindLine(stderr, "HEAD commit check failed"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// LEGACY converses of the preflight rejections that round 1 covered only on the seam path:
    /// a detached HEAD must fail identically when the commands are routed as opaque strings.
    /// </summary>
    [Theory]
    [InlineData("HEAD")]
    [InlineData("refs/tags/v1")]
    public async Task Improver_LegacyPath_Preparation_DetachedHead_IsRejected(string symbolicName)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args == "rev-parse --symbolic-full-name HEAD"
                ? (0, symbolicName + "\n", "")
                : ((int, string, string)?)null,
        };
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-detached-" + symbolicName.Length, configRepoDir, git,
            agentRunner: agentRunner);

        AssertPreparationRejected(result, agentRunner, "HEAD is not attached to a usable branch");
        AssertNoLegacyMutationOrPublication(git);
    }

    /// <summary>
    /// LEGACY converse of the upstream topology rejection: missing, non-origin remote, and a
    /// mismatched branch name.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("refs/remotes/upstream/main")]
    [InlineData("refs/remotes/origin/other")]
    public async Task Improver_LegacyPath_Preparation_UpstreamProblem_IsRejected(string? upstream)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args == "rev-parse --symbolic-full-name @{upstream}"
                ? (upstream is null
                    ? (128, "", "fatal: no upstream configured for branch 'main'")
                    : (0, upstream + "\n", ""))
                : ((int, string, string)?)null,
        };
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-upstream-" + (upstream?.Length ?? 0), configRepoDir, git,
            agentRunner: agentRunner);

        var expected = upstream is null
            ? "Config repo preparation upstream check failed (exit 128)"
            : "the branch has no matching origin upstream";
        AssertPreparationRejected(result, agentRunner, expected);
        AssertNoLegacyMutationOrPublication(git);
    }

    // ── Fetch failure: no stale FETCH_HEAD may ever be consulted ──────────────

    /// <summary>
    /// THE CRITICAL FETCH-FAILURE INVARIANT. When the exact-branch fetch fails, a
    /// <c>FETCH_HEAD</c> left over from a PREVIOUS successful fetch must never be resolved: the
    /// fake would happily answer <c>rev-parse --verify FETCH_HEAD^{commit}</c> with a perfectly
    /// valid stale SHA, and a <c>reset --hard</c> to it would look successful. Production must
    /// abort at the failed fetch, so that resolution is NEVER issued, no reset happens from
    /// local or remote-tracking evidence, and nothing is published.
    /// </summary>
    [Theory]
    [InlineData(true)]  // seam (tokenized) route
    [InlineData(false)] // legacy (opaque) route
    public async Task Improver_Preparation_FailedFetch_NeverResolvesAStaleFetchHead(bool viaSeam)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var agentRunner = new MockAgentRunner();
        TaskResult result;
        List<string> issuedCommands;

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake
            {
                Responder = tokens => tokens switch
                {
                    ["fetch", ..] => new GitProcessResult(128, "", "fatal: could not read from remote"),
                    // A STALE but perfectly valid FETCH_HEAD from an earlier run. If production
                    // ever asked, it would get a usable SHA — it must never ask.
                    ["rev-parse", "--verify", "FETCH_HEAD^{commit}"] =>
                        new GitProcessResult(0, StaleFetchHeadSha + "\n", ""),
                    _ => null,
                },
            };
            using var seam = CreateConfigRepoSeam(configRepoDir);
            var git = new MockGitOperations();
            (result, _, _) = await RunImproverWithSeamAsync(
                "improver-seam-fetch-fail-stale", configRepoDir, seam, fake, git,
                agentRunner: agentRunner);

            Assert.Empty(git.GitCommands);
            AssertNoPublicationLaunched(fake);
            Assert.DoesNotContain(fake.Launched, t => t is ["reset", ..]);
            Assert.DoesNotContain(fake.Launched, t => t is ["clean", ..]);
            issuedCommands = [.. fake.Launched.Select(t => string.Join(' ', t))];
        }
        else
        {
            var git = new MockGitOperations
            {
                GitCommandResponder = args => args.StartsWith("fetch ", StringComparison.Ordinal)
                    ? (128, "", "fatal: could not read from remote")
                    : args == "rev-parse --verify FETCH_HEAD^{commit}"
                        ? (0, StaleFetchHeadSha + "\n", "")
                        : ((int, string, string)?)null,
            };
            (result, _, _) = await RunImproverLegacyAsync(
                "improver-legacy-fetch-fail-stale", configRepoDir, git, agentRunner: agentRunner);

            AssertNoLegacyPublication(git);
            Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("reset", StringComparison.Ordinal));
            Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("clean", StringComparison.Ordinal));
            issuedCommands = git.GitCommands;
        }

        AssertPreparationRejected(
            result, agentRunner, "Config repo preparation fetch failed (exit 128)");

        // THE POINT: the stale FETCH_HEAD was never resolved after the failed fetch, and the
        // stale SHA never appeared in ANY issued command (so no reset could target it).
        Assert.DoesNotContain(
            issuedCommands, c => c.Contains("FETCH_HEAD", StringComparison.Ordinal));
        Assert.DoesNotContain(
            issuedCommands, c => c.Contains(StaleFetchHeadSha, StringComparison.Ordinal));

        // The fetch itself was attempted exactly once — no retry from stale evidence.
        Assert.Single(issuedCommands, c => c.StartsWith("fetch", StringComparison.Ordinal));
    }

    /// <summary>A valid-looking SHA left in FETCH_HEAD by a PREVIOUS run — never a usable baseline.</summary>
    private const string StaleFetchHeadSha = "4444444444444444444444444444444444444444";

    /// <summary>
    /// FETCH_HEAD output quality: every malformed shape is rejected with the same single-line
    /// 40/64-hex rule that governs <c>HEAD^{commit}</c>. Exit zero is NOT enough.
    /// </summary>
    [Theory]
    [InlineData("")]                                                              // blank
    [InlineData("\n")]                                                            // newline only
    [InlineData("   \t  \n")]                                                     // whitespace only
    [InlineData("4444444\n")]                                                     // abbreviated
    [InlineData("444444444444444444444444444444444444444\n")]                     // 39 chars
    [InlineData("44444444444444444444444444444444444444444\n")]                   // 41 chars
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz\n")]                    // non-hex
    [InlineData("refs/heads/main\n")]                                             // a ref, not a SHA
    [InlineData(
        "4444444444444444444444444444444444444444\n5555555555555555555555555555555555555555\n")] // ambiguous
    public async Task Improver_SeamPath_Preparation_MalformedFetchHeadOutput_IsRejected(string stdout)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens is ["rev-parse", "--verify", "FETCH_HEAD^{commit}"]
                ? new GitProcessResult(0, stdout, "")
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            $"improver-seam-badfetchhead-{stdout.Length}", configRepoDir, seam, fake, git,
            agentRunner: agentRunner);

        AssertPreparationRejected(
            result, agentRunner, "FETCH_HEAD did not resolve to a single full commit SHA");

        // The malformed baseline stops the restore: no reset from unusable evidence.
        Assert.DoesNotContain(fake.Launched, t => t is ["reset", ..]);
        Assert.DoesNotContain(fake.Launched, t => t is ["clean", ..]);
        AssertNoPublicationLaunched(fake);
    }

    // ── Destructive/verification step failures ───────────────────────────────

    /// <summary>
    /// A failed <c>reset --hard</c> is a truthful preparation failure: the clean never runs
    /// (there is nothing verified to clean up to), and nothing is published.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Improver_Preparation_FailedReset_IsSanitizedFailure(bool viaSeam)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var agentRunner = new MockAgentRunner();

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake
            {
                Responder = tokens => tokens is ["reset", ..]
                    ? new GitProcessResult(128, "", "fatal: Could not reset index file to revision")
                    : null,
            };
            using var seam = CreateConfigRepoSeam(configRepoDir);
            var git = new MockGitOperations();

            var (result, _, _) = await RunImproverWithSeamAsync(
                "improver-seam-reset-fail", configRepoDir, seam, fake, git, agentRunner: agentRunner);

            AssertPreparationRejected(
                result, agentRunner, "Config repo preparation reset failed (exit 128)");
            // The clean never ran after the failed reset, and nothing was published.
            Assert.DoesNotContain(fake.Launched, t => t is ["clean", ..]);
            AssertNoPublicationLaunched(fake);
            // Exactly ONE preparation reset attempt — no retry, no escalation. (The step-end
            // cleanup's own verified reset is a separate, later, verified sequence.)
            Assert.Equal(2, fake.Launched.Count(t => t is ["reset", ..]));
        }
        else
        {
            var git = new MockGitOperations
            {
                GitCommandResponder = args => args.StartsWith("reset ", StringComparison.Ordinal)
                    ? (128, "", "fatal: Could not reset index file to revision")
                    : ((int, string, string)?)null,
            };

            var (result, _, _) = await RunImproverLegacyAsync(
                "improver-legacy-reset-fail", configRepoDir, git, agentRunner: agentRunner);

            AssertPreparationRejected(
                result, agentRunner, "Config repo preparation reset failed (exit 128)");
            Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("clean", StringComparison.Ordinal));
            AssertNoLegacyPublication(git);
            // Exactly ONE preparation reset attempt — no retry, no escalation. (The step-end
            // cleanup's own verified reset is a separate, later, verified sequence.)
            Assert.Equal(2, git.GitCommands.Count(c => c.StartsWith("reset ", StringComparison.Ordinal)));
        }
    }

    /// <summary>
    /// A failed <c>clean -fdx</c> — the PROTECTED NESTED REPOSITORY case, where git refuses to
    /// remove a nested repository — is reported truthfully. Critically there is NO second,
    /// escalated clean (<c>-ffdx</c> or any repeat), no arbitrary recursive deletion, and no
    /// publication: production reports the failure instead of guessing that force would work.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Improver_Preparation_FailedClean_NestedRepo_ReportsTruthfullyWithoutEscalation(
        bool viaSeam)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        const string nestedRepoStderr =
            "warning: failed to remove nested-repo/: Directory not empty";
        var agentRunner = new MockAgentRunner();

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake
            {
                Responder = tokens => tokens is ["clean", ..]
                    ? new GitProcessResult(1, "", nestedRepoStderr)
                    : null,
            };
            using var seam = CreateConfigRepoSeam(configRepoDir);
            var git = new MockGitOperations();

            var (result, _, _) = await RunImproverWithSeamAsync(
                "improver-seam-clean-fail", configRepoDir, seam, fake, git, agentRunner: agentRunner);

            AssertPreparationRejected(
                result, agentRunner, "Config repo preparation clean failed (exit 1)");

            // NO ESCALATION: exactly one PREPARATION clean, and it was the single-force
            // `-fdx` form only. (The step-end cleanup's own verified clean is a separate,
            // later, verified sequence.)
            Assert.Equal(2, fake.Launched.Count(t => t is ["clean", ..]));
            Assert.All(
                fake.Launched.Where(t => t is ["clean", ..]),
                t => Assert.Equal(["clean", "-fdx"], t));
            Assert.DoesNotContain(fake.Launched, t => t is ["clean", "-ffdx"]);
            // Neither phase's post-restore verification ever ran on an unclean tree, and
            // nothing was published.
            Assert.DoesNotContain(fake.Launched, t => t is ["status", ..]);
            AssertNoPublicationLaunched(fake);
        }
        else
        {
            var git = new MockGitOperations
            {
                GitCommandResponder = args => args.StartsWith("clean", StringComparison.Ordinal)
                    ? (1, "", nestedRepoStderr)
                    : ((int, string, string)?)null,
            };

            var (result, _, _) = await RunImproverLegacyAsync(
                "improver-legacy-clean-fail", configRepoDir, git, agentRunner: agentRunner);

            AssertPreparationRejected(
                result, agentRunner, "Config repo preparation clean failed (exit 1)");
            Assert.Equal(2, git.GitCommands.Count(c => c.StartsWith("clean", StringComparison.Ordinal)));
            Assert.All(
                git.GitCommands.Where(c => c.StartsWith("clean", StringComparison.Ordinal)),
                c => Assert.Equal("clean -fdx", c));
            Assert.DoesNotContain(git.GitCommands, c => c.Contains("-ffdx", StringComparison.Ordinal));
            AssertNoLegacyPublication(git);
        }
    }

    /// <summary>
    /// The agents working directory is recreated after <c>clean -fdx</c>. When that creation
    /// FAILS — here because a FILE already occupies the agents path, so
    /// <see cref="Directory.CreateDirectory(string)"/> throws — preparation fails truthfully
    /// with the sanitized classification (type name only, no raw message), the post-restore
    /// verification never runs, and nothing is published.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_Preparation_AgentsDirCreationFailure_IsSanitizedFailure()
    {
        var configRepoDir = Path.Combine(
            Path.GetTempPath(), $"copilothive-test-agentsfile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(configRepoDir, ".git"));
        using var remover = new DirectoryRemover(configRepoDir);

        // A FILE where the agents DIRECTORY must be: Directory.CreateDirectory throws IOException.
        await File.WriteAllTextAsync(
            Path.Combine(configRepoDir, "agents"), "not a directory",
            TestContext.Current.CancellationToken);

        var fake = new SeamProcessRunnerFake();
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-agentsdir-fail", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        AssertPreparationRejected(
            result, agentRunner, "failed to create the agents working directory");
        // SANITIZED: an exception CLASSIFICATION only — no raw filesystem message text.
        Assert.Contains(result.Metrics!.Issues, i => i.Contains("IOException", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Metrics.Issues, i => i.Contains("not a directory", StringComparison.Ordinal));

        // The reset/clean ran (they precede the creation), but the agents-directory creation
        // happens BEFORE any restore evidence is captured — so no cleanup target exists and
        // the step-end cleanup must NEVER run (fail-before-mutation is preserved).
        Assert.Contains(fake.Launched, t => t is ["reset", ..]);
        Assert.Contains(fake.Launched, t => t is ["clean", ..]);
        Assert.Single(fake.Launched, t => t is ["status", ..]);
        AssertNoPublicationLaunched(fake);
    }

    /// <summary>
    /// LEGACY converse of the post-restore HEAD mismatch: the restored HEAD differs from the
    /// captured fetched baseline, so preparation fails before prompting.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_Preparation_HeadMismatchAfterRestore_IsRejected()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var headCalls = 0;
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args == "rev-parse --verify HEAD^{commit}"
                ? (0,
                   (++headCalls == 1 ? ConfigRepoPreparationFakes.BaselineSha : LocalAheadSha) + "\n",
                   "")
                : ((int, string, string)?)null,
        };
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-head-mismatch", configRepoDir, git, agentRunner: agentRunner);

        AssertPreparationRejected(
            result, agentRunner, "HEAD does not match the fetched baseline after the restore");
        // The status verification never ran, and nothing was published.
        Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("status", StringComparison.Ordinal));
        AssertNoLegacyPublication(git);
    }

    /// <summary>
    /// A failing post-restore verification COMMAND (not merely dirty output): a non-zero
    /// verbose <c>status</c> is a preparation failure too — success is never assumed from an
    /// unreadable status.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_Preparation_FailedStatusCommand_IsRejected()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens is ["status", ..]
                ? new GitProcessResult(128, "", "fatal: unable to read index")
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-status-cmd-fail", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        AssertPreparationRejected(
            result, agentRunner, "Config repo preparation post-restore status check failed (exit 128)");
        AssertNoPublicationLaunched(fake);
    }

    // ── Exit-zero-with-empty-output must never become a usable baseline ───────

    /// <summary>
    /// NO STALE-BASELINE ACCEPTANCE. For EVERY output-consuming preparation step, a command
    /// that succeeds (exit 0) but returns EMPTY output must be rejected — an exit code alone is
    /// never evidence. One vector per consuming step: top-level, HEAD^{commit},
    /// symbolic-full-name HEAD, @{upstream}, FETCH_HEAD^{commit}.
    /// </summary>
    [Theory]
    [InlineData("toplevel", "the worktree root could not be determined")]
    [InlineData("head", "HEAD did not resolve to a single full commit SHA")]
    [InlineData("symbolic", "HEAD is not attached to a usable branch")]
    [InlineData("upstream", "the branch has no matching origin upstream")]
    [InlineData("fetchhead", "FETCH_HEAD did not resolve to a single full commit SHA")]
    public async Task Improver_SeamPath_Preparation_ExitZeroEmptyOutput_IsNeverAUsableBaseline(
        string step, string expectedReason)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            // Exit ZERO with EMPTY stdout for exactly the step under test.
            Responder = tokens => MatchesPreparationStep(tokens, step)
                ? new GitProcessResult(0, "", "")
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            $"improver-seam-emptyout-{step}", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        AssertPreparationRejected(result, agentRunner, expectedReason);
        AssertNoPublicationLaunched(fake);

        // The steps BEFORE the destructive restore must additionally have mutated nothing.
        if (step is not "fetchhead")
            Assert.DoesNotContain(fake.Launched, t => t is ["reset", ..]);
    }

    /// <summary>
    /// The LEGACY converse of the exit-zero/empty-output rule, over the same five consuming
    /// steps — the opaque route has no validation of its own, so TaskExecutor's own checks are
    /// the only thing standing between blank output and a bogus baseline.
    /// </summary>
    [Theory]
    [InlineData("toplevel", "the worktree root could not be determined")]
    [InlineData("head", "HEAD did not resolve to a single full commit SHA")]
    [InlineData("symbolic", "HEAD is not attached to a usable branch")]
    [InlineData("upstream", "the branch has no matching origin upstream")]
    [InlineData("fetchhead", "FETCH_HEAD did not resolve to a single full commit SHA")]
    public async Task Improver_LegacyPath_Preparation_ExitZeroEmptyOutput_IsNeverAUsableBaseline(
        string step, string expectedReason)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var opaque = step switch
        {
            "toplevel" => "rev-parse --show-toplevel",
            "head" => "rev-parse --verify HEAD^{commit}",
            "symbolic" => "rev-parse --symbolic-full-name HEAD",
            "upstream" => "rev-parse --symbolic-full-name @{upstream}",
            "fetchhead" => "rev-parse --verify FETCH_HEAD^{commit}",
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, "unknown preparation step"),
        };
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args == opaque ? (0, "", "") : ((int, string, string)?)null,
        };
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverLegacyAsync(
            $"improver-legacy-emptyout-{step}", configRepoDir, git, agentRunner: agentRunner);

        AssertPreparationRejected(result, agentRunner, expectedReason);
        AssertNoLegacyPublication(git);

        if (step is not "fetchhead")
            Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("reset", StringComparison.Ordinal));
    }

    /// <summary>Whether the tokenized command is the preparation step named by <paramref name="step"/>.</summary>
    private static bool MatchesPreparationStep(IReadOnlyList<string> tokens, string step) => step switch
    {
        "toplevel" => tokens is ["rev-parse", "--show-toplevel"],
        "head" => tokens is ["rev-parse", "--verify", "HEAD^{commit}"],
        "symbolic" => tokens is ["rev-parse", "--symbolic-full-name", "HEAD"],
        "upstream" => tokens is ["rev-parse", "--symbolic-full-name", "@{upstream}"],
        "fetchhead" => tokens is ["rev-parse", "--verify", "FETCH_HEAD^{commit}"],
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "unknown preparation step"),
    };

    // ── Happy path: a fully valid sequence lets the agent run ─────────────────

    /// <summary>
    /// HAPPY PATH on BOTH routes: with every preparation command answering validly, the agent
    /// callback IS invoked (exactly once for the improver's single prompt), the working
    /// directory handed to it is the agents folder, and the task completes with no preparation
    /// failure recorded.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Improver_Preparation_ValidSequence_LetsTheAgentRun(bool viaSeam)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var agentRunner = new MockAgentRunner();
        TaskResult result;

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake
            {
                Responder = tokens => tokens[0] == "diff" ? new GitProcessResult(0, "", "") : null,
            };
            using var seam = CreateConfigRepoSeam(configRepoDir);
            var git = new MockGitOperations();
            (result, _, _) = await RunImproverWithSeamAsync(
                "improver-seam-happy", configRepoDir, seam, fake, git, agentRunner: agentRunner);
        }
        else
        {
            var git = new MockGitOperations
            {
                GitCommandResponder = args =>
                    args == "diff --cached --name-only -z" ? (0, "", "") : ((int, string, string)?)null,
            };
            (result, _, _) = await RunImproverLegacyAsync(
                "improver-legacy-happy", configRepoDir, git, agentRunner: agentRunner);
        }

        // The agent RAN — preparation did not block it.
        var call = Assert.Single(agentRunner.PromptCalls);
        Assert.Equal(Path.Combine(configRepoDir, "agents"), call.WorkDir);

        // A genuine no-change completion, with no preparation failure anywhere in the result.
        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Equal("PASS", result.Metrics!.Verdict);
        Assert.DoesNotContain(
            result.Metrics.Issues, i => i.Contains("Config repo preparation", StringComparison.Ordinal));
        Assert.DoesNotContain("[Config Repo Git Failure]", result.Output);
        Assert.Equal("Mock agent response", result.Output);
    }

    /// <summary>
    /// The agents working directory is RECREATED by preparation when <c>clean -fdx</c> would
    /// have removed it, so the agent is always prompted into an existing directory.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_Preparation_RecreatesTheAgentsDirectoryBeforePrompting()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var agentsDir = Path.Combine(configRepoDir, "agents");

        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens =>
            {
                // Simulate what a real `clean -fdx` does to an untracked agents/ folder.
                if (tokens is ["clean", ..] && Directory.Exists(agentsDir))
                    Directory.Delete(agentsDir, recursive: true);
                return tokens[0] == "diff" ? new GitProcessResult(0, "", "") : null;
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentsDirExistedAtPrompt = false;
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, workDir, _) =>
            {
                agentsDirExistedAtPrompt = Directory.Exists(workDir);
                return Task.FromResult("Mock agent response");
            },
        };

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-agentsdir-recreated", configRepoDir, seam, fake, git,
            agentRunner: agentRunner);

        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.True(
            agentsDirExistedAtPrompt,
            "the agents working directory did not exist when the agent was prompted");
        // The step-end cleanup runs its own `clean -fdx` against an UNTRACKED agents/ folder
        // (per this fake's model), so the verified-clean end state legitimately has no agents
        // directory — the workspace is left at the baseline, and the verified-clean status
        // oracle is what preparation recreates the directory BEFORE, at prompt time.
        Assert.False(Directory.Exists(agentsDir));
    }

    // ── Shared assertions for the round-2 vectors ────────────────────────────

    /// <summary>No publication command was launched through the SEAM.</summary>
    private static void AssertNoPublicationLaunched(SeamProcessRunnerFake fake)
    {
        Assert.DoesNotContain(fake.Launched, t => t is ["add", ..]);
        Assert.DoesNotContain(fake.Launched, t => t is ["diff", ..]);
        Assert.DoesNotContain(fake.Launched, t => t is ["commit", ..]);
        Assert.DoesNotContain(fake.Launched, t => t is ["pull", ..]);
        Assert.DoesNotContain(fake.Launched, t => t is ["push", ..]);
        Assert.DoesNotContain(fake.Launched, t => t is ["checkout", ..]);
    }

    /// <summary>No publication command was issued on the LEGACY opaque route.</summary>
    private static void AssertNoLegacyPublication(MockGitOperations git)
    {
        Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("add ", StringComparison.Ordinal));
        Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("diff ", StringComparison.Ordinal));
        Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("commit ", StringComparison.Ordinal));
        Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("pull", StringComparison.Ordinal));
        Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("push", StringComparison.Ordinal));
        Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("checkout", StringComparison.Ordinal));
    }

    /// <summary>A LEGACY preflight rejection mutated nothing and published nothing.</summary>
    private static void AssertNoLegacyMutationOrPublication(MockGitOperations git)
    {
        Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("fetch", StringComparison.Ordinal));
        Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("reset", StringComparison.Ordinal));
        Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("clean", StringComparison.Ordinal));
        AssertNoLegacyPublication(git);
    }

    // ── (l) Iteration-3 fix 1: the ROOT consumer accepts INTERNAL whitespace ──
    //
    // A legitimate configured repository may live at a path containing an internal
    // space (`/tmp/config repo`, `C:\Users\Jane Doe\config-repo`). `rev-parse
    // --show-toplevel` reports that exact path, and canonical equality succeeds, so
    // preparation must proceed. The SHA/ref consumers keep their stricter
    // no-whitespace-anywhere domain, and PADDED roots stay rejected for everyone.

    /// <summary>
    /// FIX 1 (isolated): a worktree root containing an INTERNAL space is ACCEPTED — preparation
    /// runs to completion and the agent is prompted — on BOTH routes. The configured root and
    /// the reported top-level are the same internal-space path, so canonical equality holds.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Improver_Preparation_InternalSpaceRoot_IsAcceptedOnBothRoutes(bool viaSeam)
    {
        // A configured config-repo directory whose NAME genuinely contains a space.
        var configRepoDir = Path.Combine(
            Path.GetTempPath(), $"copilothive test config {Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(configRepoDir, ".git"));
        Directory.CreateDirectory(Path.Combine(configRepoDir, "agents"));
        using var remover = new DirectoryRemover(configRepoDir);
        Assert.Contains(' ', Path.GetFileName(configRepoDir));

        var agentRunner = new MockAgentRunner();
        TaskResult result;

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake
            {
                Responder = tokens => tokens[0] == "diff" ? new GitProcessResult(0, "", "") : null,
            };
            using var seam = CreateConfigRepoSeam(configRepoDir);
            var git = new MockGitOperations();
            (result, _, _) = await RunImproverWithSeamAsync(
                "improver-seam-spaceroot", configRepoDir, seam, fake, git, agentRunner: agentRunner);
        }
        else
        {
            var git = new MockGitOperations
            {
                GitCommandResponder = args =>
                    args == "diff --cached --name-only -z" ? (0, "", "") : ((int, string, string)?)null,
            };
            (result, _, _) = await RunImproverLegacyAsync(
                "improver-legacy-spaceroot", configRepoDir, git, agentRunner: agentRunner);
        }

        // ACCEPTED: preparation completed and the agent was prompted in the agents folder.
        Assert.Equal(TaskOutcome.Completed, result.Status);
        var call = Assert.Single(agentRunner.PromptCalls);
        Assert.Equal(Path.Combine(configRepoDir, "agents"), call.WorkDir);
        Assert.DoesNotContain(
            result.Metrics!.Issues,
            i => i.Contains("the worktree root", StringComparison.Ordinal));
    }

    /// <summary>
    /// FIX 1 boundary: INTERNAL whitespace is legal for the ROOT consumer ONLY. The same
    /// internal-space value is still MALFORMED for a SHA and for a ref, and a PADDED root is
    /// still rejected — the widening admits internal whitespace and nothing else.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Improver_Preparation_InternalWhitespace_StaysIllegalForShaAndRef(bool viaSeam)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        // (a) A SHA carrying an internal space is malformed.
        await AssertPreparationStepRejectedAsync(
            viaSeam, configRepoDir, "head",
            "1111111111111111111111 111111111111111111\n",
            "HEAD did not resolve to a single full commit SHA",
            "spacesha");

        // (b) A REF carrying an internal space is malformed.
        await AssertPreparationStepRejectedAsync(
            viaSeam, configRepoDir, "symbolic",
            "refs/heads/ma in\n",
            "HEAD is not attached to a usable branch",
            "spaceref");

        // (c) A PADDED root is STILL rejected — internal-space acceptance did not regress into
        // accepting leading/trailing whitespace.
        await AssertPreparationStepRejectedAsync(
            viaSeam, configRepoDir, "toplevel",
            "  " + ConfigRepoPreparationFakes.CanonicalRoot(configRepoDir) + "  \n",
            "the worktree root could not be determined",
            "paddedroot");
    }

    /// <summary>
    /// Drives one preparation step's stdout to <paramref name="stdout"/> on the chosen route and
    /// asserts the sanitized rejection, with the agent never prompted.
    /// </summary>
    private static async Task AssertPreparationStepRejectedAsync(
        bool viaSeam,
        string configRepoDir,
        string step,
        string stdout,
        string expectedReason,
        string label)
    {
        var agentRunner = new MockAgentRunner();
        var opaque = step switch
        {
            "toplevel" => "rev-parse --show-toplevel",
            "head" => "rev-parse --verify HEAD^{commit}",
            "symbolic" => "rev-parse --symbolic-full-name HEAD",
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, "unknown preparation step"),
        };
        TaskResult result;

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake
            {
                Responder = tokens => MatchesPreparationStep(tokens, step)
                    ? new GitProcessResult(0, stdout, "")
                    : null,
            };
            using var seam = CreateConfigRepoSeam(configRepoDir);
            var git = new MockGitOperations();
            (result, _, _) = await RunImproverWithSeamAsync(
                $"improver-seam-{label}", configRepoDir, seam, fake, git, agentRunner: agentRunner);
        }
        else
        {
            var git = new MockGitOperations
            {
                GitCommandResponder = args => args == opaque ? (0, stdout, "") : ((int, string, string)?)null,
            };
            (result, _, _) = await RunImproverLegacyAsync(
                $"improver-legacy-{label}", configRepoDir, git, agentRunner: agentRunner);
        }

        AssertPreparationRejected(result, agentRunner, expectedReason);
    }

    /// <summary>
    /// FIX 1 end-to-end with REAL GIT: a worker clone whose directory name genuinely contains a
    /// space. Real <c>rev-parse --show-toplevel</c> reports that exact path, preparation
    /// succeeds, residue is still fully removed, and the agent is prompted from the remote
    /// baseline.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealGit_Preparation_InternalSpaceWorktreeRoot_CompletesAndPrompts(bool viaSeam)
    {
        using var playground = RealGitPlayground.Create(
            $"spaceroot-{(viaSeam ? "seam" : "legacy")}",
            "REMOTE-BASELINE-V1\n",
            workerDirName: "worker clone dir");
        var worker = playground.WorkerDir;

        // The configured root REALLY contains an internal space, and real git reports it.
        Assert.Contains(' ', Path.GetFileName(worker));
        var reportedRoot = RealGitOutput(worker, "rev-parse", "--show-toplevel");
        Assert.Contains(' ', reportedRoot);

        // Residue must still be removed from a space-named checkout.
        File.WriteAllText(playground.GuidancePath, "LOCAL-EDIT\n");
        File.WriteAllText(Path.Combine(worker, "untracked.txt"), "untracked-residue\n");
        var remoteSha = playground.RemoteMainSha();

        string? seenHead = null;
        string? seenGuidance = null;
        string? seenStatus = null;
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) =>
            {
                seenHead = playground.WorkerHeadSha();
                seenGuidance = File.ReadAllText(playground.GuidancePath);
                seenStatus = playground.WorkerVerboseStatus();
                return Task.FromResult("Mock agent response");
            },
        };

        var (result, _, _) = await RunRealGitImproverAsync(
            $"realgit-spaceroot-{viaSeam}", playground, viaSeam, agentRunner);

        // The space-named root did NOT block preparation.
        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Single(agentRunner.PromptCalls);
        Assert.DoesNotContain(
            result.Metrics!.Issues,
            i => i.Contains("the worktree root", StringComparison.Ordinal));

        // The agent still started from the remote baseline with all residue gone.
        Assert.Equal(remoteSha, seenHead);
        Assert.Equal("REMOTE-BASELINE-V1\n", seenGuidance);
        Assert.Equal("", seenStatus);
        Assert.False(File.Exists(Path.Combine(worker, "untracked.txt")));
    }

    // ── (j) Iteration-2 defect fixes: strict parsing + complete ref validation ─
    //
    // DEFECT 1 — output parsing must be EXACT: only `\n` / `\r\n` terminate the single
    // line, functional content is never trimmed, and the post-restore status oracle is
    // strict EMPTY (whitespace-only output is residual dirt, not "clean").
    //
    // DEFECT 2 — the discovered ref must get git's AUTHORITATIVE check-ref-format verdict
    // on BOTH routes before either builds a fetch form.

    /// <summary>
    /// DEFECT 1 — PADDED SHA. A <c>rev-parse --verify HEAD^{commit}</c> whose output carries
    /// whitespace around the SHA is MALFORMED and must be rejected, not silently trimmed into a
    /// valid baseline. Covers leading, trailing and both-sides padding with spaces and tabs, on
    /// BOTH routes, with zero agent prompts and zero publication.
    /// </summary>
    [Theory]
    [InlineData(true, " 1111111111111111111111111111111111111111 \n")]
    [InlineData(true, " 1111111111111111111111111111111111111111\n")]
    [InlineData(true, "1111111111111111111111111111111111111111 \n")]
    [InlineData(true, "\t1111111111111111111111111111111111111111\n")]
    [InlineData(true, "1111111111111111111111111111111111111111\t\n")]
    [InlineData(true, "  1111111111111111111111111111111111111111  ")]
    [InlineData(false, " 1111111111111111111111111111111111111111 \n")]
    [InlineData(false, " 1111111111111111111111111111111111111111\n")]
    [InlineData(false, "1111111111111111111111111111111111111111 \n")]
    [InlineData(false, "\t1111111111111111111111111111111111111111\n")]
    [InlineData(false, "  1111111111111111111111111111111111111111  ")]
    public async Task Improver_Preparation_PaddedHeadSha_IsRejectedOnBothRoutes(
        bool viaSeam, string paddedStdout)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var agentRunner = new MockAgentRunner();
        TaskResult result;

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake
            {
                Responder = tokens => tokens is ["rev-parse", "--verify", "HEAD^{commit}"]
                    ? new GitProcessResult(0, paddedStdout, "")
                    : null,
            };
            using var seam = CreateConfigRepoSeam(configRepoDir);
            var git = new MockGitOperations();
            (result, _, _) = await RunImproverWithSeamAsync(
                $"improver-seam-paddedsha-{paddedStdout.Length}", configRepoDir, seam, fake, git,
                agentRunner: agentRunner);

            AssertNoMutationLaunched(fake);
            AssertNoPublicationLaunched(fake);
        }
        else
        {
            var git = new MockGitOperations
            {
                GitCommandResponder = args => args == "rev-parse --verify HEAD^{commit}"
                    ? (0, paddedStdout, "")
                    : ((int, string, string)?)null,
            };
            (result, _, _) = await RunImproverLegacyAsync(
                $"improver-legacy-paddedsha-{paddedStdout.Length}", configRepoDir, git,
                agentRunner: agentRunner);

            AssertNoLegacyMutationOrPublication(git);
        }

        AssertPreparationRejected(
            result, agentRunner, "HEAD did not resolve to a single full commit SHA");
    }

    /// <summary>
    /// DEFECT 1 — PADDED FETCH_HEAD SHA. The fetched baseline is the ONLY permitted reset
    /// target, so a padded <c>FETCH_HEAD^{commit}</c> must never be trimmed into one: no reset
    /// and no clean may follow.
    /// </summary>
    [Theory]
    [InlineData(true, " 2222222222222222222222222222222222222222 \n")]
    [InlineData(true, "2222222222222222222222222222222222222222 \n")]
    [InlineData(false, " 2222222222222222222222222222222222222222 \n")]
    [InlineData(false, "\t2222222222222222222222222222222222222222\n")]
    public async Task Improver_Preparation_PaddedFetchHeadSha_IsRejectedBeforeAnyReset(
        bool viaSeam, string paddedStdout)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var agentRunner = new MockAgentRunner();
        TaskResult result;

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake
            {
                Responder = tokens => tokens is ["rev-parse", "--verify", "FETCH_HEAD^{commit}"]
                    ? new GitProcessResult(0, paddedStdout, "")
                    : null,
            };
            using var seam = CreateConfigRepoSeam(configRepoDir);
            var git = new MockGitOperations();
            (result, _, _) = await RunImproverWithSeamAsync(
                $"improver-seam-paddedfetch-{paddedStdout.Length}", configRepoDir, seam, fake, git,
                agentRunner: agentRunner);

            // The padded value never became a reset target.
            Assert.DoesNotContain(fake.Launched, t => t is ["reset", ..]);
            Assert.DoesNotContain(fake.Launched, t => t is ["clean", ..]);
            Assert.DoesNotContain(
                fake.Launched,
                t => t.Any(a => a.Contains("2222222222222222222222222222222222222222", StringComparison.Ordinal)));
            AssertNoPublicationLaunched(fake);
        }
        else
        {
            var git = new MockGitOperations
            {
                GitCommandResponder = args => args == "rev-parse --verify FETCH_HEAD^{commit}"
                    ? (0, paddedStdout, "")
                    : ((int, string, string)?)null,
            };
            (result, _, _) = await RunImproverLegacyAsync(
                $"improver-legacy-paddedfetch-{paddedStdout.Length}", configRepoDir, git,
                agentRunner: agentRunner);

            Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("reset", StringComparison.Ordinal));
            Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("clean", StringComparison.Ordinal));
            Assert.DoesNotContain(
                git.GitCommands,
                c => c.Contains("2222222222222222222222222222222222222222", StringComparison.Ordinal));
            AssertNoLegacyPublication(git);
        }

        AssertPreparationRejected(
            result, agentRunner, "FETCH_HEAD did not resolve to a single full commit SHA");
    }

    /// <summary>
    /// DEFECT 1 — PADDED REF. A padded <c>rev-parse --symbolic-full-name HEAD</c> value must be
    /// rejected rather than trimmed into <c>refs/heads/main</c>: a branch name is functional
    /// content and is never normalized.
    /// </summary>
    [Theory]
    [InlineData(true, " refs/heads/main \n")]
    [InlineData(true, " refs/heads/main\n")]
    [InlineData(true, "refs/heads/main \n")]
    [InlineData(true, "\trefs/heads/main\n")]
    [InlineData(false, " refs/heads/main \n")]
    [InlineData(false, "refs/heads/main \n")]
    [InlineData(false, "\trefs/heads/main\n")]
    public async Task Improver_Preparation_PaddedBranchRef_IsRejectedOnBothRoutes(
        bool viaSeam, string paddedStdout)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var agentRunner = new MockAgentRunner();
        TaskResult result;

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake
            {
                Responder = tokens => tokens is ["rev-parse", "--symbolic-full-name", "HEAD"]
                    ? new GitProcessResult(0, paddedStdout, "")
                    : null,
            };
            using var seam = CreateConfigRepoSeam(configRepoDir);
            var git = new MockGitOperations();
            (result, _, _) = await RunImproverWithSeamAsync(
                $"improver-seam-paddedref-{paddedStdout.Length}", configRepoDir, seam, fake, git,
                agentRunner: agentRunner);

            AssertNoMutationLaunched(fake);
            AssertNoPublicationLaunched(fake);
        }
        else
        {
            var git = new MockGitOperations
            {
                GitCommandResponder = args => args == "rev-parse --symbolic-full-name HEAD"
                    ? (0, paddedStdout, "")
                    : ((int, string, string)?)null,
            };
            (result, _, _) = await RunImproverLegacyAsync(
                $"improver-legacy-paddedref-{paddedStdout.Length}", configRepoDir, git,
                agentRunner: agentRunner);

            AssertNoLegacyMutationOrPublication(git);
        }

        AssertPreparationRejected(result, agentRunner, "HEAD is not attached to a usable branch");
    }

    /// <summary>
    /// DEFECT 1 — PADDED UPSTREAM and PADDED TOP-LEVEL ROOT. The remaining two output-consuming
    /// preflight steps reject padding for the same reason.
    /// </summary>
    [Theory]
    [InlineData(true, "upstream")]
    [InlineData(false, "upstream")]
    [InlineData(true, "toplevel")]
    [InlineData(false, "toplevel")]
    public async Task Improver_Preparation_PaddedUpstreamOrRoot_IsRejectedOnBothRoutes(
        bool viaSeam, string step)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var agentRunner = new MockAgentRunner();
        var padded = step == "upstream"
            ? " refs/remotes/origin/main \n"
            : " " + ConfigRepoPreparationFakes.CanonicalRoot(configRepoDir) + " \n";
        var expectedReason = step == "upstream"
            ? "the branch has no matching origin upstream"
            : "the worktree root could not be determined";
        var opaque = step == "upstream"
            ? "rev-parse --symbolic-full-name @{upstream}"
            : "rev-parse --show-toplevel";
        TaskResult result;

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake
            {
                Responder = tokens => MatchesPreparationStep(tokens, step)
                    ? new GitProcessResult(0, padded, "")
                    : null,
            };
            using var seam = CreateConfigRepoSeam(configRepoDir);
            var git = new MockGitOperations();
            (result, _, _) = await RunImproverWithSeamAsync(
                $"improver-seam-padded-{step}", configRepoDir, seam, fake, git,
                agentRunner: agentRunner);

            AssertNoMutationLaunched(fake);
            AssertNoPublicationLaunched(fake);
        }
        else
        {
            var git = new MockGitOperations
            {
                GitCommandResponder = args => args == opaque ? (0, padded, "") : ((int, string, string)?)null,
            };
            (result, _, _) = await RunImproverLegacyAsync(
                $"improver-legacy-padded-{step}", configRepoDir, git, agentRunner: agentRunner);

            AssertNoLegacyMutationOrPublication(git);
        }

        AssertPreparationRejected(result, agentRunner, expectedReason);
    }

    /// <summary>
    /// DEFECT 1 — EXTRA LINES. A valid SHA followed by a blank, whitespace-only or padded
    /// second line is AMBIGUOUS output. The old parser discarded empty entries and accepted the
    /// remaining line; strict parsing rejects every one of these on both routes.
    /// </summary>
    [Theory]
    [InlineData(true, "1111111111111111111111111111111111111111\n\n")]
    [InlineData(true, "1111111111111111111111111111111111111111\n \n")]
    [InlineData(true, "1111111111111111111111111111111111111111\n\t\n")]
    [InlineData(true, "\n1111111111111111111111111111111111111111\n")]
    [InlineData(true, " \n1111111111111111111111111111111111111111\n")]
    [InlineData(true, "1111111111111111111111111111111111111111\r\n\r\n")]
    [InlineData(true, "1111111111111111111111111111111111111111\n1111111111111111111111111111111111111111\n")]
    [InlineData(false, "1111111111111111111111111111111111111111\n\n")]
    [InlineData(false, "1111111111111111111111111111111111111111\n \n")]
    [InlineData(false, "\n1111111111111111111111111111111111111111\n")]
    [InlineData(false, "1111111111111111111111111111111111111111\r\n\r\n")]
    public async Task Improver_Preparation_ExtraLineHeadSha_IsRejectedOnBothRoutes(
        bool viaSeam, string multilineStdout)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var agentRunner = new MockAgentRunner();
        TaskResult result;

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake
            {
                Responder = tokens => tokens is ["rev-parse", "--verify", "HEAD^{commit}"]
                    ? new GitProcessResult(0, multilineStdout, "")
                    : null,
            };
            using var seam = CreateConfigRepoSeam(configRepoDir);
            var git = new MockGitOperations();
            (result, _, _) = await RunImproverWithSeamAsync(
                $"improver-seam-extraline-{multilineStdout.Length}-{multilineStdout.GetHashCode()}",
                configRepoDir, seam, fake, git, agentRunner: agentRunner);

            AssertNoMutationLaunched(fake);
            AssertNoPublicationLaunched(fake);
        }
        else
        {
            var git = new MockGitOperations
            {
                GitCommandResponder = args => args == "rev-parse --verify HEAD^{commit}"
                    ? (0, multilineStdout, "")
                    : ((int, string, string)?)null,
            };
            (result, _, _) = await RunImproverLegacyAsync(
                $"improver-legacy-extraline-{multilineStdout.Length}-{multilineStdout.GetHashCode()}",
                configRepoDir, git, agentRunner: agentRunner);

            AssertNoLegacyMutationOrPublication(git);
        }

        AssertPreparationRejected(
            result, agentRunner, "HEAD did not resolve to a single full commit SHA");
    }

    /// <summary>
    /// DEFECT 1 — the PERMITTED terminators still work. A bare SHA, a <c>\n</c>-terminated SHA
    /// and a <c>\r\n</c>-terminated SHA are all ACCEPTED, so the strict parser rejects malformed
    /// output without rejecting the normal shapes git actually emits. The whole preparation
    /// completes and the agent runs.
    /// </summary>
    [Theory]
    [InlineData("1111111111111111111111111111111111111111")]
    [InlineData("1111111111111111111111111111111111111111\n")]
    [InlineData("1111111111111111111111111111111111111111\r\n")]
    public async Task Improver_SeamPath_Preparation_PermittedTerminators_AreAccepted(string stdout)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens switch
            {
                ["rev-parse", "--verify", "HEAD^{commit}"] => new GitProcessResult(0, stdout, ""),
                ["rev-parse", "--verify", "FETCH_HEAD^{commit}"] => new GitProcessResult(0, stdout, ""),
                ["diff", ..] => new GitProcessResult(0, "", ""),
                _ => null,
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            $"improver-seam-terminator-{stdout.Length}", configRepoDir, seam, fake, git,
            agentRunner: agentRunner);

        // Accepted: the sequence completed and the agent ran.
        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Single(agentRunner.PromptCalls);
        // The reset target is the UNPADDED, unterminated SHA.
        Assert.Contains(
            fake.Launched, t => t is ["reset", "--hard", "1111111111111111111111111111111111111111"]);
    }

    /// <summary>
    /// DEFECT 1 — WHITESPACE-ONLY STATUS. The verified-clean oracle is strict EMPTY output.
    /// A status that returns only whitespace is NOT clean: it is unrecognized output and must
    /// be reported as a residual dirty tree, with NO escalation to a second clean and no
    /// publication. Both routes.
    /// </summary>
    [Theory]
    [InlineData(true, " ")]
    [InlineData(true, "\n")]
    [InlineData(true, "   \n")]
    [InlineData(true, "\t")]
    [InlineData(true, "\r\n")]
    [InlineData(true, " \t \n ")]
    [InlineData(false, " ")]
    [InlineData(false, "\n")]
    [InlineData(false, "   \n")]
    [InlineData(false, "\t")]
    [InlineData(false, " \t \n ")]
    public async Task Improver_Preparation_WhitespaceOnlyStatus_IsResidualDirt(
        bool viaSeam, string statusStdout)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var agentRunner = new MockAgentRunner();
        TaskResult result;

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake
            {
                Responder = tokens => tokens is ["status", "--porcelain=v1", ..]
                    ? new GitProcessResult(0, statusStdout, "")
                    : null,
            };
            using var seam = CreateConfigRepoSeam(configRepoDir);
            var git = new MockGitOperations();
            (result, _, _) = await RunImproverWithSeamAsync(
                $"improver-seam-wsstatus-{statusStdout.Length}-{statusStdout.GetHashCode()}",
                configRepoDir, seam, fake, git, agentRunner: agentRunner);

            // NO ESCALATION: exactly one PREPARATION clean and one PREPARATION status, and
            // nothing published. (The step-end cleanup's own clean + status are a separate,
            // later, verified sequence.)
            Assert.Equal(2, fake.Launched.Count(t => t is ["clean", ..]));
            Assert.All(
                fake.Launched.Where(t => t is ["clean", ..]),
                t => Assert.Equal(["clean", "-fdx"], t));
            Assert.Equal(2, fake.Launched.Count(t => t is ["status", ..]));
            AssertNoPublicationLaunched(fake);
        }
        else
        {
            var git = new MockGitOperations
            {
                GitCommandResponder = args =>
                    args == "status --porcelain=v1 --untracked-files=all --ignored"
                        ? (0, statusStdout, "")
                        : ((int, string, string)?)null,
            };
            (result, _, _) = await RunImproverLegacyAsync(
                $"improver-legacy-wsstatus-{statusStdout.Length}-{statusStdout.GetHashCode()}",
                configRepoDir, git, agentRunner: agentRunner);

            // NO ESCALATION: exactly one PREPARATION clean, never `-ffdx`. (The step-end
            // cleanup's own clean is a separate, later, verified sequence.)
            Assert.Equal(2, git.GitCommands.Count(c => c.StartsWith("clean", StringComparison.Ordinal)));
            Assert.All(
                git.GitCommands.Where(c => c.StartsWith("clean", StringComparison.Ordinal)),
                c => Assert.Equal("clean -fdx", c));
            Assert.DoesNotContain(git.GitCommands, c => c.Contains("-ffdx", StringComparison.Ordinal));
            AssertNoLegacyPublication(git);
        }

        AssertPreparationRejected(
            result, agentRunner, "the working tree is not clean after the restore");
    }

    // ── DEFECT 2: the discovered ref gets git's authoritative verdict on BOTH routes ──

    /// <summary>
    /// DEFECT 2 — MALFORMED DISCOVERED REF. These refs pass the seam's cheap PRECHECKS but are
    /// rejected by <c>git check-ref-format</c> itself (verified against the real binary:
    /// <c>refs/heads/foo.lock</c>, <c>refs/heads/.hidden</c>, <c>refs/heads/foo//bar</c> and
    /// <c>refs/heads/foo@{bar}</c> all exit non-zero). Before the fix the LEGACY route — which
    /// never ran check-ref-format — happily built and launched a fetch for them. Now the
    /// preflight applies the authoritative verdict on BOTH routes, so NO fetch is ever
    /// launched, nothing is mutated, and the agent is never prompted.
    /// </summary>
    [Theory]
    [InlineData(true, "refs/heads/foo.lock")]
    [InlineData(true, "refs/heads/.hidden")]
    [InlineData(true, "refs/heads/foo//bar")]
    [InlineData(true, "refs/heads/foo@{bar}")]
    [InlineData(false, "refs/heads/foo.lock")]
    [InlineData(false, "refs/heads/.hidden")]
    [InlineData(false, "refs/heads/foo//bar")]
    [InlineData(false, "refs/heads/foo@{bar}")]
    public async Task Improver_Preparation_MalformedDiscoveredRef_IsRejectedBeforeFetchOnBothRoutes(
        bool viaSeam, string discoveredRef)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var agentRunner = new MockAgentRunner();
        var branch = discoveredRef["refs/heads/".Length..];
        TaskResult result;

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake
            {
                // The REAL check-ref-format verdict for these refs is a non-zero exit.
                CheckRefFormatExitCode = 1,
                // The topology is otherwise PERFECT — the upstream matches the discovered
                // branch — so the ONLY thing that can stop the fetch is the ref validation.
                Responder = tokens => tokens switch
                {
                    ["rev-parse", "--symbolic-full-name", "HEAD"] =>
                        new GitProcessResult(0, discoveredRef + "\n", ""),
                    ["rev-parse", "--symbolic-full-name", "@{upstream}"] =>
                        new GitProcessResult(0, "refs/remotes/origin/" + branch + "\n", ""),
                    _ => null,
                },
            };
            using var seam = CreateConfigRepoSeam(configRepoDir);
            var git = new MockGitOperations();
            (result, _, _) = await RunImproverWithSeamAsync(
                $"improver-seam-badref-{branch.Length}-{branch.GetHashCode()}", configRepoDir,
                seam, fake, git, agentRunner: agentRunner);

            // The authoritative check ran, and NO fetch followed it.
            Assert.Contains(
                fake.Launched, t => t is ["check-ref-format", "--allow-onelevel", _]);
            Assert.DoesNotContain(fake.Launched, t => t is ["fetch", ..]);
            AssertNoMutationLaunched(fake);
            AssertNoPublicationLaunched(fake);
        }
        else
        {
            var git = new MockGitOperations
            {
                // The topology is otherwise PERFECT — the upstream matches the discovered
                // branch — so the ONLY thing that can stop the fetch is the ref validation.
                GitCommandResponder = args => args switch
                {
                    "rev-parse --symbolic-full-name HEAD" => (0, discoveredRef + "\n", ""),
                    "rev-parse --symbolic-full-name @{upstream}" =>
                        (0, "refs/remotes/origin/" + branch + "\n", ""),
                    _ => ((int, string, string)?)null,
                },
            };
            // The LEGACY route runs the real `git check-ref-format` binary through the shared
            // validation path (GitOperations.ProcessRunner is NOT installed here), so this is
            // git's own verdict — the exact defect: before the fix this fetched anyway.
            (result, _, _) = await RunImproverLegacyAsync(
                $"improver-legacy-badref-{branch.Length}-{branch.GetHashCode()}", configRepoDir,
                git, agentRunner: agentRunner);

            Assert.DoesNotContain(git.GitCommands, c => c.StartsWith("fetch", StringComparison.Ordinal));
            Assert.DoesNotContain(
                git.GitCommands, c => c.Contains(discoveredRef, StringComparison.Ordinal));
            AssertNoLegacyMutationOrPublication(git);
        }

        AssertPreparationRejected(
            result, agentRunner, "the discovered branch ref is not a valid git ref");
    }

    /// <summary>
    /// DEFECT 2 — the rejection is SANITIZED: the reason names the ref through the seam's
    /// existing redaction boundary and carries no raw stderr or exception text, and the log
    /// line cannot be forged into extra lines.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_Preparation_MalformedDiscoveredRef_IsSanitized()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args == "rev-parse --symbolic-full-name HEAD"
                ? (0, "refs/heads/foo.lock\n", "")
                : ((int, string, string)?)null,
        };
        var agentRunner = new MockAgentRunner();

        var (result, _, stderr) = await RunImproverLegacyAsync(
            "improver-legacy-badref-sanitized", configRepoDir, git, agentRunner: agentRunner);

        AssertPreparationRejected(
            result, agentRunner, "the discovered branch ref is not a valid git ref");

        // The reason is a single rendered log line — no forged lines, no raw git stderr.
        var line = FindLine(stderr, "the discovered branch ref is not a valid git ref");
        Assert.Contains("Invalid git ref", line, StringComparison.Ordinal);
        Assert.DoesNotContain("fatal:", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// DEFECT 2 — a VALID discovered ref still proceeds: the authoritative check passes and the
    /// fetch is launched with that exact ref, so the fix rejects only what git rejects.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Improver_Preparation_ValidDiscoveredRef_StillProceedsToFetch(bool viaSeam)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var agentRunner = new MockAgentRunner();

        if (viaSeam)
        {
            var fake = new SeamProcessRunnerFake
            {
                Responder = tokens => tokens[0] == "diff" ? new GitProcessResult(0, "", "") : null,
            };
            using var seam = CreateConfigRepoSeam(configRepoDir);
            var git = new MockGitOperations();
            var (result, _, _) = await RunImproverWithSeamAsync(
                "improver-seam-goodref", configRepoDir, seam, fake, git, agentRunner: agentRunner);

            Assert.Equal(TaskOutcome.Completed, result.Status);
            Assert.Contains(
                fake.Launched, t => t is ["fetch", "origin", ConfigRepoPreparationFakes.BranchRef]);
            Assert.Single(agentRunner.PromptCalls);
        }
        else
        {
            var git = new MockGitOperations
            {
                GitCommandResponder = args =>
                    args == "diff --cached --name-only -z" ? (0, "", "") : ((int, string, string)?)null,
            };
            var (result, _, _) = await RunImproverLegacyAsync(
                "improver-legacy-goodref", configRepoDir, git, agentRunner: agentRunner);

            Assert.Equal(TaskOutcome.Completed, result.Status);
            Assert.Contains(
                $"fetch origin \"{ConfigRepoPreparationFakes.BranchRef}\"", git.GitCommands);
            Assert.Single(agentRunner.PromptCalls);
        }
    }


    // ══════════════════════════════════════════════════════════════════════════
    // (k) REAL-GIT INTEGRATION MATRIX — the ACTUAL preparation caller
    //
    // Every vector below drives TaskExecutor.ExecuteAsync (never a hand-rolled
    // command recipe) against a real, isolated LOCAL BARE remote and a real
    // disposable worker clone. No network, no live worker process, no live
    // config repository, no process-kill. Each test owns its temp tree and
    // tears it down in a finally block.
    //
    // The config repo URL resolver is pointed at the local bare remote path. A
    // local path sanitizes to an INELIGIBLE (Branch B) transport URL, so the
    // seam launches the snapshot verbatim with no credential injection — which
    // is exactly what an offline test needs.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A disposable real-Git playground: an isolated bare remote, a worker clone tracking it,
    /// and an OUTSIDE sentinel directory that no preparation may ever touch.
    /// </summary>
    private sealed class RealGitPlayground : IDisposable
    {
        private RealGitPlayground(string root, string remoteDir, string workerDir, string outsideDir)
        {
            Root = root;
            RemoteDir = remoteDir;
            WorkerDir = workerDir;
            OutsideDir = outsideDir;
        }

        public string Root { get; }

        /// <summary>The isolated LOCAL bare remote (the only "remote" any of these tests use).</summary>
        public string RemoteDir { get; }

        /// <summary>The worker's config-repo clone — the directory preparation operates on.</summary>
        public string WorkerDir { get; }

        /// <summary>A directory OUTSIDE the worker clone, holding an untouchable sentinel.</summary>
        public string OutsideDir { get; }

        /// <summary>The sentinel file outside the repository.</summary>
        public string OutsideSentinelPath => Path.Combine(OutsideDir, "outside-sentinel.txt");

        /// <summary>The committed guidance file, relative to the worker clone.</summary>
        public const string GuidanceRelativePath = "agents/coder.agents.md";

        public string GuidancePath => Path.Combine(WorkerDir, "agents", "coder.agents.md");

        /// <summary>
        /// Builds the playground: a bare remote seeded through a staging clone with a committed
        /// <c>agents/</c> guidance file, then a worker clone of that remote.
        /// </summary>
        public static RealGitPlayground Create(
            string label,
            string remoteGuidanceContent,
            string workerDirName = "worker",
            bool seedIgnoreRuleAndStagedBaseline = false)
        {
            var root = Path.Combine(
                Path.GetTempPath(), $"cghive-realgit-{label}-{Guid.NewGuid():N}");
            var remoteDir = Path.Combine(root, "remote.git");
            var stagingDir = Path.Combine(root, "staging");
            var workerDir = Path.Combine(root, workerDirName);
            var outsideDir = Path.Combine(root, "outside");

            Directory.CreateDirectory(root);
            Directory.CreateDirectory(remoteDir);
            Directory.CreateDirectory(stagingDir);
            Directory.CreateDirectory(outsideDir);

            RealGit(root, "init", "--bare", "-b", "main", remoteDir);

            RealGit(stagingDir, "init", "-b", "main");
            ConfigureIdentity(stagingDir);
            Directory.CreateDirectory(Path.Combine(stagingDir, "agents"));
            File.WriteAllText(
                Path.Combine(stagingDir, "agents", "coder.agents.md"), remoteGuidanceContent);

            if (seedIgnoreRuleAndStagedBaseline)
            {
                // The ignore RULE is committed to the REMOTE baseline, so `ignored.txt` stays
                // genuinely IGNORED after `reset --hard` — a local-only .gitignore would be
                // removed by the restore and the file would degrade into merely-untracked,
                // collapsing the ignored cell into the untracked one.
                File.WriteAllText(Path.Combine(stagingDir, ".gitignore"), "ignored.txt\n");
                // A TRACKED file at the remote baseline, so a later edit + `git add` in the
                // worker produces a genuine HEAD-vs-index delta (the staged cell).
                File.WriteAllText(Path.Combine(stagingDir, "staged.txt"), StagedBaselineContent);
            }

            RealGit(stagingDir, "add", "-A");
            RealGit(stagingDir, "commit", "-m", "remote baseline");
            RealGit(stagingDir, "remote", "add", "origin", remoteDir);
            RealGit(stagingDir, "push", "origin", "main");

            RealGit(root, "clone", remoteDir, workerDir);
            ConfigureIdentity(workerDir);

            File.WriteAllText(
                Path.Combine(outsideDir, "outside-sentinel.txt"), "outside-untouched\n");

            return new RealGitPlayground(root, remoteDir, workerDir, outsideDir);
        }

        /// <summary>The remote-baseline content of the tracked <c>staged.txt</c> fixture file.</summary>
        public const string StagedBaselineContent = "REMOTE-STAGED-BASELINE\n";

        /// <summary>The bare remote's current <c>main</c> SHA — the authoritative value.</summary>
        public string RemoteMainSha() => RealGitOutput(RemoteDir, "rev-parse", "main").Trim();

        /// <summary>The worker clone's current HEAD SHA.</summary>
        public string WorkerHeadSha() => RealGitOutput(WorkerDir, "rev-parse", "HEAD").Trim();

        /// <summary>The worker clone's <c>refs/remotes/origin/main</c> SHA.</summary>
        public string WorkerOriginMainSha() =>
            RealGitOutput(WorkerDir, "rev-parse", "refs/remotes/origin/main").Trim();

        /// <summary>The verbose status output — the verified-clean oracle.</summary>
        public string WorkerVerboseStatus() => RealGitOutput(
            WorkerDir, "status", "--porcelain=v1", "--untracked-files=all", "--ignored");

        /// <summary>Whether <paramref name="sha"/> is reachable from the bare remote's main.</summary>
        public bool RemoteMainContains(string sha)
        {
            var (exitCode, _, _) = RealGitResult(
                RemoteDir, "merge-base", "--is-ancestor", sha, "main");
            return exitCode == 0;
        }

        /// <summary>Whether the worker clone still has <paramref name="sha"/> as a commit object.</summary>
        public bool WorkerHasCommit(string sha)
        {
            var (exitCode, _, _) = RealGitResult(WorkerDir, "cat-file", "-e", sha + "^{commit}");
            return exitCode == 0;
        }

        /// <summary>Advances the bare remote by one commit, through a throwaway staging clone.</summary>
        public string AdvanceRemote(string newGuidanceContent, string message)
        {
            var pusherDir = Path.Combine(Root, $"pusher-{Guid.NewGuid():N}");
            Directory.CreateDirectory(pusherDir);
            RealGit(Root, "clone", RemoteDir, pusherDir);
            ConfigureIdentity(pusherDir);
            Directory.CreateDirectory(Path.Combine(pusherDir, "agents"));
            File.WriteAllText(
                Path.Combine(pusherDir, "agents", "coder.agents.md"), newGuidanceContent);
            RealGit(pusherDir, "add", "-A");
            RealGit(pusherDir, "commit", "-m", message);
            RealGit(pusherDir, "push", "origin", "main");
            return RemoteMainSha();
        }

        public void Dispose()
        {
            try
            {
                TestHelpers.ForceDeleteDirectory(Root);
            }
            catch (Exception)
            {
                // Best-effort cleanup of test scaffolding; never mask a test failure. Leftovers
                // live under the OS temp directory.
            }
        }

        private static void ConfigureIdentity(string workDir)
        {
            RealGit(workDir, "config", "user.email", "realgit@copilothive.test");
            RealGit(workDir, "config", "user.name", "CopilotHive RealGit Tests");
        }
    }

    /// <summary>
    /// Runs a real git subprocess and THROWS when it fails, so fixture setup can never proceed
    /// on a broken assumption. Pinned config keeps the behaviour identical on any host.
    /// </summary>
    private static void RealGit(string workDir, params string[] args)
    {
        var (exitCode, stdout, stderr) = RealGitResult(workDir, args);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed in '{workDir}' (exit {exitCode}).{Environment.NewLine}{stdout}{stderr}");
        }
    }

    /// <summary>Runs a real git subprocess and returns stdout, throwing on failure.</summary>
    private static string RealGitOutput(string workDir, params string[] args)
    {
        var (exitCode, stdout, stderr) = RealGitResult(workDir, args);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed in '{workDir}' (exit {exitCode}).{Environment.NewLine}{stdout}{stderr}");
        }

        return stdout;
    }

    /// <summary>
    /// Runs a real git subprocess and returns its full result WITHOUT throwing. The pinned
    /// <c>-c</c> settings mirror the repository's existing real-Git helpers: deterministic line
    /// endings, bare-repository access, and no commit signing (a host with a global
    /// <c>commit.gpgsign=true</c> would otherwise contend for the GPG agent under parallel runs).
    /// </summary>
    private static (int ExitCode, string Stdout, string Stderr) RealGitResult(
        string workDir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.autocrlf=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.bareRepository=all");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("commit.gpgsign=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("protocol.file.allow=always");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Parses verbose porcelain-v1 output into <c>path -&gt; two-character XY status</c>, so each
    /// residue cell can be pinned INDEPENDENTLY (<c> M</c> tracked-modified, <c>M </c> staged
    /// index delta, <c>??</c> untracked, <c>!!</c> ignored) rather than by a loose substring.
    /// </summary>
    private static Dictionary<string, string> ParsePorcelain(string porcelain)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = line.TrimEnd('\r');
            if (entry.Length < 4)
                continue;

            // Format: "XY <path>" — X is the index status, Y the worktree status.
            map[entry[3..]] = entry[..2];
        }

        return map;
    }

    /// <summary>
    /// against a real worker clone, with real git subprocesses (the
    /// <see cref="GitOperations.ProcessRunner"/> seam is NOT installed). Routing is chosen by
    /// <paramref name="viaSeam"/>: the injected config-repo seam (tokenized) or the public
    /// constructor (legacy opaque). Console output is captured for the sanitization assertions.
    /// </summary>
    private static async Task<(TaskResult Result, string Stderr, IReadOnlyList<string> LegacyCommands)>
        RunRealGitImproverAsync(
            string taskId,
            RealGitPlayground playground,
            bool viaSeam,
            MockAgentRunner agentRunner)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        // The legacy route runs through the REAL git CLI via DefaultGitOperations, wrapped so the
        // issued opaque command strings are observable. The seam route uses the mock only as the
        // unused non-config git dependency.
        var recordingGit = new RecordingRealGitOperations();

        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errWriter);

            TaskExecutor executor;
            ConfigRepoGitOperations? seam = null;
            if (viaSeam)
            {
                seam = new ConfigRepoGitOperations(
                    playground.WorkerDir,
                    () => playground.RemoteDir,
                    static () => null,
                    new WorkerLogger("RealGitTest"),
                    static () => "/nonexistent-helper",
                    static () => { });
                executor = new TaskExecutor(
                    agentRunner, null, recordingGit, null, playground.WorkerDir, seam);
            }
            else
            {
                executor = new TaskExecutor(
                    agentRunner, gitOperations: recordingGit, configRepoDir: playground.WorkerDir);
            }

            try
            {
                var result = await executor.ExecuteAsync(
                    BuildImproverTask(taskId), TestContext.Current.CancellationToken);
                return (result, errWriter.ToString(), recordingGit.Commands);
            }
            finally
            {
                seam?.Dispose();
            }
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    /// <summary>
    /// A REAL <see cref="IGitOperations"/> (delegating to <see cref="DefaultGitOperations"/>, so
    /// actual git subprocesses run) that additionally RECORDS every opaque command string the
    /// legacy config-repo route issues. That recording is what makes "exactly one clean -fdx,
    /// no -ffdx escalation" and "no reset --hard &lt;stale-SHA&gt;" directly observable.
    /// </summary>
    private sealed class RecordingRealGitOperations : IGitOperations
    {
        private readonly DefaultGitOperations _inner = new();
        private readonly List<string> _commands = [];

        public IReadOnlyList<string> Commands => _commands;

        public async Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(
            string workDir, string args, CancellationToken ct)
        {
            _commands.Add(args);
            return await _inner.RunGitCommandAsync(workDir, args, ct);
        }

        public Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct)
            => _inner.CloneRepositoryAsync(url, targetDir, ct);
        public Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct)
            => _inner.CheckoutBranchAsync(repoDir, branch, ct);
        public Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct)
            => _inner.CreateBranchAsync(repoDir, branchName, baseBranch, ct);
        public Task PushBranchAsync(string repoDir, string branch, CancellationToken ct)
            => _inner.PushBranchAsync(repoDir, branch, ct);
        public Task<GitChangeSummary> GetGitStatusAsync(string repoDir, string? baseBranch, CancellationToken ct)
            => _inner.GetGitStatusAsync(repoDir, baseBranch, ct);
        public Task<bool> HasUncommittedChangesAsync(string repoDir, CancellationToken ct)
            => _inner.HasUncommittedChangesAsync(repoDir, ct);
        public Task<string?> GetMergeBaseAsync(string repoDir, string baseBranch, CancellationToken ct)
            => _inner.GetMergeBaseAsync(repoDir, baseBranch, ct);
        public Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5)
            => _inner.ForceDeleteDirectoryAsync(path, maxRetries);
    }

    // ── (a) Full residue removal before the first prompt ──────────────────────

    /// <summary>
    /// REAL GIT (a): a checkout carrying EVERY residue class — a modified tracked file, a staged
    /// file, an untracked file, an ignored file, and a local commit that is either AHEAD of or
    /// DIVERGED from the remote — is fully restored to the freshly fetched remote baseline
    /// before the agent's first prompt.
    /// <para>
    /// The agent callback observes the working tree AT PROMPT TIME, so the assertions describe
    /// what the agent actually sees, not merely the end state.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true, false)]   // seam route, local commit AHEAD of the remote
    [InlineData(false, false)]  // legacy route, local commit AHEAD of the remote
    [InlineData(true, true)]    // seam route, DIVERGED (local commit + remote advanced)
    [InlineData(false, true)]   // legacy route, DIVERGED
    public async Task RealGit_Preparation_RemovesEveryResidueClassBeforeTheFirstPrompt(
        bool viaSeam, bool diverged)
    {
        using var playground = RealGitPlayground.Create(
            $"residue-{(viaSeam ? "seam" : "legacy")}-{(diverged ? "div" : "ahead")}",
            "REMOTE-BASELINE-V1\n",
            seedIgnoreRuleAndStagedBaseline: true);
        var worker = playground.WorkerDir;

        // ── Seed EVERY residue class, in an ORDER that keeps each cell genuine ──
        //
        // The ahead-commit is created FIRST so it cannot consume the residue: everything below
        // is created AFTER it and therefore really is present at preparation entry. (Staging a
        // file BEFORE the commit and then rewriting the same bytes leaves NO index delta at
        // all — the porcelain assertion below is what pins that.)
        //
        // 1. A local commit AHEAD of the remote (its own file, so no residue is absorbed).
        File.WriteAllText(Path.Combine(worker, "ahead.txt"), "ahead-commit-content\n");
        RealGit(worker, "add", "ahead.txt");
        RealGit(worker, "commit", "-m", "local ahead commit");
        var localAheadSha = playground.WorkerHeadSha();

        // For the DIVERGED variant the remote independently advances too.
        var remoteSha = diverged
            ? playground.AdvanceRemote("REMOTE-BASELINE-V2-ADVANCED\n", "remote advanced")
            : playground.RemoteMainSha();

        // 2. A modified TRACKED file (worktree-only, uncommitted).
        File.WriteAllText(playground.GuidancePath, "LOCAL-UNCOMMITTED-EDIT\n");
        // 3. A genuine STAGED INDEX DELTA: `staged.txt` is tracked at the remote baseline, so
        //    changing its content and staging it produces a real HEAD-vs-index difference.
        File.WriteAllText(Path.Combine(worker, "staged.txt"), "STAGED-INDEX-DELTA\n");
        RealGit(worker, "add", "staged.txt");
        // 4. An UNTRACKED file.
        File.WriteAllText(Path.Combine(worker, "untracked.txt"), "untracked-residue\n");
        // 5. An IGNORED file. The ignore RULE lives in the REMOTE baseline (committed there),
        //    so `ignored.txt` stays genuinely IGNORED after the reset — it never degrades into
        //    a merely-untracked file, which would collapse cell 5 into cell 4.
        File.WriteAllText(Path.Combine(worker, "ignored.txt"), "ignored-residue\n");

        // ── PIN each residue cell INDEPENDENTLY before preparation runs ────────
        var preStatus = ParsePorcelain(playground.WorkerVerboseStatus());
        Assert.Equal(" M", preStatus["agents/coder.agents.md"]);   // tracked-modified, worktree only
        Assert.Equal("M ", preStatus["staged.txt"]);               // STAGED: a real index delta
        Assert.Equal("??", preStatus["untracked.txt"]);            // untracked
        Assert.Equal("!!", preStatus["ignored.txt"]);              // genuinely ignored
        // The staged cell is an INDEX delta, not merely a worktree edit: git diff --cached
        // names the file, which a commit-consumed staging would not.
        Assert.Contains(
            "staged.txt",
            RealGitOutput(worker, "diff", "--cached", "--name-only"),
            StringComparison.Ordinal);

        Assert.NotEqual(remoteSha, localAheadSha);
        var outsideSentinelBefore = File.ReadAllText(playground.OutsideSentinelPath);

        // ── What the AGENT sees at prompt time ────────────────────────────────
        string? seenGuidance = null;
        string? seenHead = null;
        string? seenStatus = null;
        string? seenStagedDiff = null;
        var seenStaged = true;
        var seenUntracked = true;
        var seenIgnored = true;
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) =>
            {
                seenGuidance = File.ReadAllText(playground.GuidancePath);
                seenHead = playground.WorkerHeadSha();
                seenStatus = playground.WorkerVerboseStatus();
                seenStagedDiff = RealGitOutput(worker, "diff", "--cached", "--name-only");
                seenStaged = File.Exists(Path.Combine(worker, "staged.txt"))
                    && File.ReadAllText(Path.Combine(worker, "staged.txt")) == "STAGED-INDEX-DELTA\n";
                seenUntracked = File.Exists(Path.Combine(worker, "untracked.txt"));
                seenIgnored = File.Exists(Path.Combine(worker, "ignored.txt"));
                return Task.FromResult("Mock agent response");
            },
        };

        var (result, _, _) = await RunRealGitImproverAsync(
            $"realgit-residue-{viaSeam}-{diverged}", playground, viaSeam, agentRunner);

        // The agent RAN — preparation did not block it.
        Assert.Single(agentRunner.PromptCalls);
        Assert.Equal(TaskOutcome.Completed, result.Status);

        // ── THE RESIDUE CELLS, asserted FIRST (this test's subject) ───────────
        // PROMPT-TIME STATUS PROVES ALL FOUR CELLS ARE GONE: an EMPTY verbose porcelain means
        // no tracked-modified, no staged index delta, no untracked and no ignored residue.
        Assert.Equal("", seenStatus);
        // The STAGED cell specifically: the index carries no delta at prompt time, and the
        // tracked file is back at its remote-baseline content (not the staged residue). This
        // pair is what a restore that skips `reset --hard` cannot satisfy.
        Assert.Equal("", seenStagedDiff);
        Assert.False(seenStaged, "the staged residue was visible to the agent");
        Assert.False(seenUntracked, "the untracked residue was visible to the agent");
        Assert.False(seenIgnored, "the ignored residue was visible to the agent");

        // THE AGENT'S VISIBLE WORKING TREE STARTS FROM THE REMOTE BASELINE.
        Assert.Equal(remoteSha, seenHead);
        Assert.Equal(
            diverged ? "REMOTE-BASELINE-V2-ADVANCED\n" : "REMOTE-BASELINE-V1\n", seenGuidance);

        // Every residue class is gone from disk, and the local ahead-commit is no longer HEAD.
        Assert.Equal(remoteSha, playground.WorkerHeadSha());
        Assert.NotEqual(localAheadSha, playground.WorkerHeadSha());
        // `staged.txt` is TRACKED at the remote baseline, so it exists again — restored to the
        // REMOTE's content, with the staged residue discarded.
        Assert.Equal(
            RealGitPlayground.StagedBaselineContent,
            File.ReadAllText(Path.Combine(worker, "staged.txt")));
        Assert.Empty(RealGitOutput(worker, "diff", "--cached", "--name-only"));
        Assert.False(File.Exists(Path.Combine(worker, "untracked.txt")));
        Assert.False(File.Exists(Path.Combine(worker, "ignored.txt")));
        Assert.False(File.Exists(Path.Combine(worker, "ahead.txt")));
        Assert.Equal("", playground.WorkerVerboseStatus());

        // The remote guidance content is present at the REMOTE's version.
        Assert.Equal(
            diverged ? "REMOTE-BASELINE-V2-ADVANCED\n" : "REMOTE-BASELINE-V1\n",
            File.ReadAllText(playground.GuidancePath));

        // The OUTSIDE sentinel is untouched, byte for byte.
        Assert.True(File.Exists(playground.OutsideSentinelPath), "the outside sentinel was deleted");
        Assert.Equal(outsideSentinelBefore, File.ReadAllText(playground.OutsideSentinelPath));

        // Remote refs agree with the bare remote at the end boundary.
        Assert.Equal(remoteSha, playground.RemoteMainSha());
        Assert.Equal(remoteSha, playground.WorkerOriginMainSha());
    }

    // ── (b) Protected nested repository survives AND prevents the prompt ──────

    /// <summary>
    /// REAL GIT (b): a nested git repository inside the worker clone is PROTECTED by
    /// <c>clean -fdx</c> (git refuses to remove a directory holding its own <c>.git</c>), so it
    /// survives the clean and is then EXPOSED by the verbose status. Preparation must report
    /// that truthfully: exactly ONE <c>clean -fdx</c>, NO <c>-ffdx</c> escalation, no recursive
    /// deletion, a sanitized failure, and the agent NEVER prompted.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealGit_Preparation_ProtectedNestedRepo_SurvivesAndPreventsThePrompt(bool viaSeam)
    {
        using var playground = RealGitPlayground.Create(
            $"nested-{(viaSeam ? "seam" : "legacy")}", "REMOTE-BASELINE-V1\n");
        var worker = playground.WorkerDir;

        // A NESTED repository with its own committed content, planted as untracked residue.
        var nestedDir = Path.Combine(worker, "nested-repo");
        Directory.CreateDirectory(nestedDir);
        RealGit(nestedDir, "init", "-b", "main");
        RealGit(nestedDir, "config", "user.email", "nested@copilothive.test");
        RealGit(nestedDir, "config", "user.name", "Nested");
        File.WriteAllText(Path.Combine(nestedDir, "nested-content.txt"), "nested-precious\n");
        RealGit(nestedDir, "add", "-A");
        RealGit(nestedDir, "commit", "-m", "nested baseline");
        var nestedHeadBefore = RealGitOutput(nestedDir, "rev-parse", "HEAD").Trim();

        // Ordinary dirty residue too, so the clean demonstrably RAN.
        File.WriteAllText(Path.Combine(worker, "ordinary-dirt.txt"), "dirt\n");
        File.WriteAllText(playground.GuidancePath, "LOCAL-EDIT\n");

        var agentRunner = new MockAgentRunner();
        var (result, stderr, legacyCommands) = await RunRealGitImproverAsync(
            $"realgit-nested-{viaSeam}", playground, viaSeam, agentRunner);

        // TRUTHFUL FAILURE, and the agent was NEVER prompted.
        Assert.Empty(agentRunner.PromptCalls);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Contains(
            result.Metrics.Issues,
            i => i.Contains("the working tree is not clean after the restore", StringComparison.Ordinal));

        // SANITIZED: no raw git stderr leaked into the log or the persisted result.
        Assert.DoesNotContain("fatal:", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("fatal:", result.Output, StringComparison.Ordinal);

        // THE NESTED REPOSITORY SURVIVED, intact, with its own content and history.
        Assert.True(Directory.Exists(nestedDir), "the nested repository directory was deleted");
        Assert.True(
            Directory.Exists(Path.Combine(nestedDir, ".git")),
            "the nested repository's .git directory was removed");
        Assert.True(
            File.Exists(Path.Combine(nestedDir, "nested-content.txt")),
            "the nested repository's content was deleted");
        Assert.Equal("nested-precious\n", File.ReadAllText(Path.Combine(nestedDir, "nested-content.txt")));
        Assert.Equal(nestedHeadBefore, RealGitOutput(nestedDir, "rev-parse", "HEAD").Trim());

        // The clean really RAN: ordinary dirt is gone even though the nested repo survived.
        Assert.False(File.Exists(Path.Combine(worker, "ordinary-dirt.txt")));

        // NO ESCALATION on the legacy route, where the issued command strings are observable:
        // each phase issues exactly ONE clean, spelled `clean -fdx`, and never `-ffdx`. (The
        // step-end cleanup runs its own verified clean after the preparation rejection, since
        // the restore evidence was already captured; the nested repo survives BOTH.)
        if (!viaSeam)
        {
            var cleans = legacyCommands
                .Where(c => c.StartsWith("clean", StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(2, cleans.Length);
            Assert.All(cleans, c => Assert.Equal("clean -fdx", c));
            Assert.DoesNotContain(legacyCommands, c => c.Contains("-ffdx", StringComparison.Ordinal));
            // Nothing was published either.
            Assert.DoesNotContain(legacyCommands, c => c.StartsWith("push", StringComparison.Ordinal));
            Assert.DoesNotContain(legacyCommands, c => c.StartsWith("add ", StringComparison.Ordinal));
        }
    }

    // ── (c) Failed fetch cannot reset to stale evidence ──────────────────────

    /// <summary>
    /// REAL GIT (c): a FIRST successful fetch leaves a genuine, valid <c>FETCH_HEAD</c> on disk;
    /// the origin URL is then broken so the SECOND fetch — the one preparation issues — really
    /// fails. Preparation must abort at the failed fetch: the stale <c>FETCH_HEAD</c> must never
    /// be resolved, no <c>reset --hard</c> may run, HEAD must be unchanged, and the agent must
    /// never run.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealGit_Preparation_FailedFetch_NeverResetsToTheStaleFetchHead(bool viaSeam)
    {
        using var playground = RealGitPlayground.Create(
            $"stalefetch-{(viaSeam ? "seam" : "legacy")}", "REMOTE-BASELINE-V1\n");
        var worker = playground.WorkerDir;

        // A FIRST, genuinely successful fetch writes a real FETCH_HEAD to disk. Advance the
        // remote first so the stale FETCH_HEAD is a DIFFERENT commit from the local HEAD —
        // a reset to it would be plainly observable.
        var advancedSha = playground.AdvanceRemote("REMOTE-BASELINE-V2\n", "remote advanced");
        RealGit(worker, "fetch", "origin", "refs/heads/main");
        var staleFetchHead = RealGitOutput(worker, "rev-parse", "FETCH_HEAD^{commit}").Trim();
        Assert.Equal(advancedSha, staleFetchHead);

        // Pin the worker back to the ORIGINAL baseline so HEAD != stale FETCH_HEAD.
        var headBefore = RealGitOutput(worker, "rev-parse", "HEAD^{commit}").Trim();
        Assert.NotEqual(staleFetchHead, headBefore);

        // BREAK the origin so preparation's own fetch really fails.
        var brokenRemote = Path.Combine(playground.Root, "does-not-exist.git");
        RealGit(worker, "remote", "set-url", "origin", brokenRemote);

        var agentRunner = new MockAgentRunner();
        var (result, stderr, legacyCommands) = await RunRealGitImproverAsync(
            $"realgit-stalefetch-{viaSeam}", playground, viaSeam, agentRunner);

        // Truthful failure, agent never prompted.
        Assert.Empty(agentRunner.PromptCalls);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Contains(
            result.Metrics.Issues,
            i => i.Contains("Config repo preparation fetch failed", StringComparison.Ordinal));

        // THE INVARIANT: HEAD is untouched — no reset to the stale FETCH_HEAD ever happened.
        Assert.Equal(headBefore, RealGitOutput(worker, "rev-parse", "HEAD^{commit}").Trim());
        Assert.NotEqual(staleFetchHead, RealGitOutput(worker, "rev-parse", "HEAD^{commit}").Trim());

        // The reflog is the independent witness: no reset entry, and the stale SHA never became
        // a checkout target.
        var reflog = RealGitOutput(worker, "reflog", "--format=%gs");
        Assert.DoesNotContain("reset:", reflog, StringComparison.Ordinal);
        Assert.DoesNotContain(staleFetchHead, reflog, StringComparison.Ordinal);

        // On the legacy route the issued commands prove it directly: no FETCH_HEAD resolution
        // and no reset at all after the failed fetch.
        if (!viaSeam)
        {
            Assert.DoesNotContain(
                legacyCommands, c => c.Contains("FETCH_HEAD", StringComparison.Ordinal));
            Assert.DoesNotContain(
                legacyCommands, c => c.StartsWith("reset", StringComparison.Ordinal));
            Assert.DoesNotContain(
                legacyCommands, c => c.Contains(staleFetchHead, StringComparison.Ordinal));
        }

        // Sanitized: the failure reason carries git's stderr through RenderForLog (redacted and
        // control-sanitized), so it must occupy a SINGLE log line — no forged extra lines — and
        // must never carry a credential.
        var fetchFailureLine = FindLine(stderr, "Config repo preparation fetch failed");
        Assert.Contains("(exit ", fetchFailureLine, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SplitLines(stderr), l => l.Trim().StartsWith("fatal:", StringComparison.Ordinal));
    }

    // ── (d) The two-sequential-assignments rejected-push scenario ────────────

    /// <summary>
    /// REAL GIT (d) — THE GOAL'S CORE SCENARIO, end to end with two sequential
    /// <see cref="TaskExecutor.ExecuteAsync"/> calls against the SAME bare remote and the SAME
    /// worker clone.
    /// <para>
    /// RUN 1: the agent makes a real commit. A <c>pre-push</c> hook in the worker clone advances
    /// the bare remote between the worker's fetch and its push — the ordinary lost-race — so the
    /// push is genuinely REJECTED by git (no force push, no remote rollback, no interception of
    /// the result). The run reports a truthful publication failure and the abandoned commit
    /// remains locally.
    /// </para>
    /// <para>
    /// RUN 2: preparation must restore the checkout to the FRESHLY FETCHED remote baseline, so
    /// the abandoned run-1 commit is neither HEAD nor present in the working tree. Run 2 then
    /// publishes its own change successfully, and the abandoned commit is proven unpublishable:
    /// it is not on the remote and not reachable from run 2's pushed commit.
    /// </para>
    /// <para>
    /// Run on BOTH routes: the LEGACY opaque route and the PRODUCTION SEAM route (the real
    /// <see cref="ConfigRepoGitOperations"/>). The local bare-remote path is an INELIGIBLE
    /// Branch-B transport URL, so the seam launches the snapshot verbatim with no credential
    /// injection — the production dispatch, fully offline.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false)] // LEGACY opaque route
    [InlineData(true)]  // production SEAM route (ConfigRepoGitOperations, Branch-B local path)
    public async Task RealGit_TwoSequentialAssignments_RejectedPush_CannotPublishTheAbandonedCommit(
        bool viaSeam)
    {
        using var playground = RealGitPlayground.Create(
            $"rejectedpush-{(viaSeam ? "seam" : "legacy")}", "REMOTE-BASELINE-V1\n");
        var worker = playground.WorkerDir;

        // ── Boundary 0: the starting remote SHA ───────────────────────────────
        var remoteShaBeforeRun1 = playground.RemoteMainSha();

        // The rejected-push mechanism: a pre-push hook that advances the bare remote ONCE,
        // between this worker's fetch and its push. Git then refuses the non-fast-forward
        // update on its own — the rejection is real, not simulated.
        var interloperContent = "REMOTE-INTERLOPER-V2\n";
        var hookPath = Path.Combine(worker, ".git", "hooks", "pre-push");
        var advanceMarker = Path.Combine(worker, ".git", "ADVANCE_ONCE");
        var pusherScriptDir = Path.Combine(playground.Root, "hook-pusher");
        Directory.CreateDirectory(pusherScriptDir);
        File.WriteAllText(
            hookPath,
            $"""
            #!/bin/sh
            if [ -f '{advanceMarker}' ]; then
              rm -f '{advanceMarker}'
              rm -rf '{pusherScriptDir}/clone'
              git -c protocol.file.allow=always clone -q '{playground.RemoteDir}' '{pusherScriptDir}/clone'
              cd '{pusherScriptDir}/clone' || exit 0
              git config user.email hook@copilothive.test
              git config user.name Hook
              printf '{interloperContent.Replace("\n", "\\n")}' > agents/coder.agents.md
              git add -A
              git -c commit.gpgsign=false commit -qm 'interloper commit'
              git push -q origin main
            fi
            exit 0
            """.Replace("\r\n", "\n"));
        RealGitResult(worker, "update-index", "--chmod=+x", "--", ".git/hooks/pre-push");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        File.WriteAllText(advanceMarker, "");

        // ── RUN 1: the improver's own publication commits; the push loses the race ──
        // The agent EDITS the guidance file (its real job); TaskExecutor's publication stage
        // then performs the real add/diff/commit/pull/push. The pre-push hook advances the
        // bare remote in between, so git itself rejects the non-fast-forward update.
        var run1Agent = new MockAgentRunner
        {
            PromptResponder = (_, _, _) =>
            {
                File.WriteAllText(playground.GuidancePath, "RUN1-IMPROVER-EDIT\n");
                return Task.FromResult("Run 1 agent response");
            },
        };

        var (run1Result, _, run1LegacyCommands) = await RunRealGitImproverAsync(
            $"realgit-rejected-run1-{viaSeam}", playground, viaSeam, run1Agent);

        // ROUTE PROOF: on the seam route NOTHING reached the legacy opaque dispatch (so the
        // production ConfigRepoGitOperations really carried every config-repo command); on the
        // legacy route it carried them all.
        if (viaSeam)
            Assert.Empty(run1LegacyCommands);
        else
            Assert.NotEmpty(run1LegacyCommands);

        // The agent ran, and the publication stage really committed its edit. The abandoned
        // commit is recovered from the reflog: the newest entry is the step-end cleanup's
        // verified reset to the baseline, and the entry beneath it is the abandoned
        // publication commit.
        Assert.Single(run1Agent.PromptCalls);
        var reflogShas = RealGitOutput(worker, "reflog", "--format=%H")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(reflogShas.Length >= 2, "the run-1 reflog is missing the cleanup and commit entries");
        var abandonedSha = reflogShas[1];
        Assert.True(playground.WorkerHasCommit(abandonedSha));
        Assert.Equal(
            "RUN1-IMPROVER-EDIT\n",
            RealGitOutput(worker, "show", abandonedSha + ":agents/coder.agents.md"));

        // TRUTHFUL PUBLICATION FAILURE: the push was genuinely rejected.
        Assert.Equal(TaskOutcome.Failed, run1Result.Status);
        Assert.Equal("FAIL", run1Result.Metrics!.Verdict);
        Assert.False(run1Result.GitStatus!.Pushed);
        Assert.DoesNotContain("fatal:", run1Result.Output, StringComparison.Ordinal);

        // CLEAN-AT-STEP-END: the rejected push had no confirmed publication, so run 1's own
        // finalization already restored the checkout to the captured fetched baseline — the
        // abandoned commit is neither HEAD nor reachable from it, though its object still
        // exists (unreachable) exactly as the reflog witness above shows.
        Assert.Equal(remoteShaBeforeRun1, RealGitOutput(worker, "rev-parse", "HEAD^{commit}").Trim());
        Assert.NotEqual(abandonedSha, RealGitOutput(worker, "rev-parse", "HEAD^{commit}").Trim());

        // ── Boundary 1: the remote advanced to the interloper, NOT to run 1 ───
        var remoteShaAfterRun1 = playground.RemoteMainSha();
        Assert.NotEqual(remoteShaBeforeRun1, remoteShaAfterRun1);
        Assert.NotEqual(abandonedSha, remoteShaAfterRun1);
        Assert.False(
            playground.RemoteMainContains(abandonedSha),
            "the abandoned run-1 commit reached the remote");

        // The abandoned commit still exists LOCALLY as an unreachable object — this is exactly
        // the danger the fetch-first preparation neutralizes, and run 1's own finalization has
        // already restored the branch. Run 2's preparation still re-fetches and re-restores.

        // ── RUN 2: preparation must discard the abandoned commit ─────────────
        var remoteShaBeforeRun2 = playground.RemoteMainSha();
        string? run2SeenHead = null;
        string? run2SeenGuidance = null;
        var run2SeenAbandonedReachable = true;
        var run2Agent = new MockAgentRunner
        {
            PromptResponder = (_, _, _) =>
            {
                // OBSERVED AT THE SECOND RUN'S START, before the agent changes anything.
                run2SeenHead = RealGitOutput(worker, "rev-parse", "HEAD^{commit}").Trim();
                run2SeenGuidance = File.ReadAllText(playground.GuidancePath);
                var (ancestorExit, _, _) = RealGitResult(
                    worker, "merge-base", "--is-ancestor", abandonedSha, "HEAD");
                run2SeenAbandonedReachable = ancestorExit == 0;

                // Run 2 publishes its OWN change — again by EDITING only; the publication
                // stage does the committing and pushing.
                File.WriteAllText(playground.GuidancePath, "RUN2-IMPROVER-EDIT\n");
                return Task.FromResult("Run 2 agent response");
            },
        };

        var (run2Result, _, _) = await RunRealGitImproverAsync(
            $"realgit-rejected-run2-{viaSeam}", playground, viaSeam, run2Agent);

        Assert.Single(run2Agent.PromptCalls);

        // ── THE CORE ASSERTIONS at the second run's start ─────────────────────
        // HEAD equals the FRESHLY FETCHED remote SHA — never the abandoned commit.
        Assert.Equal(remoteShaBeforeRun2, run2SeenHead);
        Assert.NotEqual(abandonedSha, run2SeenHead);
        // The abandoned change is ABSENT from the working tree; the interloper's is present.
        Assert.Equal(interloperContent, run2SeenGuidance);
        Assert.DoesNotContain("RUN1-IMPROVER-EDIT", run2SeenGuidance, StringComparison.Ordinal);
        // The branch was restored to the remote baseline: the abandoned commit is not an
        // ancestor of the second run's starting HEAD.
        Assert.False(
            run2SeenAbandonedReachable,
            "the abandoned run-1 commit was still reachable from the second run's HEAD");

        // ── Boundary 2: the abandoned commit can never be published ───────────
        Assert.Equal(TaskOutcome.Completed, run2Result.Status);
        Assert.True(run2Result.GitStatus!.Pushed);

        var remoteShaAfterRun2 = playground.RemoteMainSha();
        Assert.NotEqual(remoteShaBeforeRun2, remoteShaAfterRun2);
        // The abandoned commit is NOT on the remote and NOT reachable from run 2's push.
        Assert.False(
            playground.RemoteMainContains(abandonedSha),
            "the abandoned run-1 commit became reachable from the remote after run 2");
        Assert.NotEqual(abandonedSha, remoteShaAfterRun2);
        // Run 2's own content IS published.
        Assert.Equal(
            "RUN2-IMPROVER-EDIT\n",
            RealGitOutput(playground.RemoteDir, "show", "main:agents/coder.agents.md"));

        // CLEAN-AT-STEP-END for run 2: the confirmed publication means finalization restored
        // the checkout to the PUBLISHED tip (not the older baseline), leaving a verified-clean
        // tree whose HEAD equals the remote.
        var finalHead = RealGitOutput(worker, "rev-parse", "HEAD^{commit}").Trim();
        Assert.Equal(remoteShaAfterRun2, finalHead);
        Assert.Equal("RUN2-IMPROVER-EDIT\n", RealGitOutput(worker, "show", "HEAD:agents/coder.agents.md"));
        Assert.Equal("", playground.WorkerVerboseStatus());

        // Remote refs agree at the final boundary.
        Assert.Equal(remoteShaAfterRun2, playground.WorkerOriginMainSha());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // FINALIZATION (clean-at-step-end) — the shared finalization path
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The distinct PUBLICATION SHA used to expose erroneous rollback of success: the fake
    /// reports this SHA for the publication-HEAD probe (after the pull), so the confirmed push
    /// publishes at <see cref="PublicationSha"/> while the captured baseline is
    /// <see cref="ConfigRepoPreparationFakes.BaselineSha"/>. A finalization that rolls the
    /// checkout back to the BASELINE on a confirmed success fails every published-tip check.
    /// </summary>
    private const string PublicationSha = "5555555555555555555555555555555555555555";

    /// <summary>
    /// The step-end cleanup's EXACT sequence on the LEGACY route for a confirmed publication
    /// whose resolved publication HEAD is <see cref="PublicationSha"/>: the trust revalidation
    /// (root, branch, upstream), the reset to the PUBLISHED SHA (never the baseline), the
    /// single clean, and the two verification probes.
    /// </summary>
    private static string[] LegacyCleanupCommandsAt(string sha) =>
    [
        "rev-parse --show-toplevel",
        "rev-parse --symbolic-full-name HEAD",
        "rev-parse --symbolic-full-name @{upstream}",
        $"reset --hard {sha}",
        "clean -fdx",
        "rev-parse --verify HEAD^{commit}",
        "status --porcelain=v1 --untracked-files=all --ignored",
    ];

    /// <summary>
    /// The tokenized step-end cleanup sequence for a confirmed publication whose resolved
    /// publication HEAD is <paramref name="sha"/>.
    /// </summary>
    private static string[][] SeamCleanupLaunchesAt(string sha) =>
    [
        ["rev-parse", "--show-toplevel"],
        ["rev-parse", "--symbolic-full-name", "HEAD"],
        ["check-ref-format", "--allow-onelevel", ConfigRepoPreparationFakes.BranchRef],
        ["rev-parse", "--symbolic-full-name", "@{upstream}"],
        ["reset", "--hard", sha],
        ["clean", "-fdx"],
        ["rev-parse", "--verify", "HEAD^{commit}"],
        ["status", "--porcelain=v1", "--untracked-files=all", "--ignored"],
    ];

    /// <summary>
    /// A sequence-aware HEAD-probe responder: the FIRST TWO <c>rev-parse --verify
    /// HEAD^{commit}</c> probes (the preparation preflight and its post-restore check) answer
    /// the captured BASELINE, and every LATER probe (the publication-HEAD resolution and the
    /// cleanup's post-restore verification) answers <see cref="PublicationSha"/> — so the
    /// confirmed push publishes a DISTINCT SHA from the baseline.
    /// </summary>
    private static (Func<IReadOnlyList<string>, GitProcessResult?> Responder, Func<int> HeadProbeCount)
        PublicationShaSequenceResponder(Func<IReadOnlyList<string>, GitProcessResult?>? inner = null)
    {
        var headCalls = 0;
        Func<IReadOnlyList<string>, GitProcessResult?> responder = tokens =>
        {
            if (tokens is ["rev-parse", "--verify", "HEAD^{commit}"])
            {
                headCalls++;
                return new GitProcessResult(0, (headCalls <= 2
                    ? ConfigRepoPreparationFakes.BaselineSha
                    : PublicationSha) + "\n", "");
            }

            return inner?.Invoke(tokens);
        };
        return (responder, () => headCalls);
    }

    /// <summary>
    /// FINALIZATION SEQUENCE (seam): a confirmed publication resolves a DISTINCT publication
    /// HEAD, and the step-end cleanup resets to THAT SHA — never to the older baseline. The
    /// full launch sequence is asserted end to end, with phase-aware cleanup probes.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_Finalization_AfterConfirmedPush_ResetsToThePublishedSha()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var (responder, headProbeCount) = PublicationShaSequenceResponder(
            tokens => tokens[0] == "diff"
                ? new GitProcessResult(0, StagedOutput("agents/coder.agents.md"), "")
                : null);
        var fake = new SeamProcessRunnerFake { Responder = responder };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-fin-published", configRepoDir, seam, fake, git);

        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.True(result.GitStatus!.Pushed);
        // The publication HEAD was resolved exactly once, before the push.
        Assert.Equal(4, headProbeCount()); // preflight, post-restore, publication, cleanup-verify

        // The CLEANUP's reset target is the PUBLISHED SHA — never the older baseline. (The
        // PREPARATION's own reset to the baseline is expected and is not a cleanup step.)
        var preparationLaunchCount = ConfigRepoPreparationFakes.SeamLaunches.Length;
        var resets = fake.Launched.Where(t => t is ["reset", ..]).ToArray();
        Assert.Equal(2, resets.Length);
        Assert.Equal(["reset", "--hard", ConfigRepoPreparationFakes.BaselineSha], resets[0]);
        Assert.Equal(["reset", "--hard", PublicationSha], resets[1]);
        // Exactly ONE cleanup clean (in addition to the preparation clean).
        Assert.Equal(2, fake.Launched.Count(t => t is ["clean", ..]));
        // The trailing cleanup sequence is exactly the expected tokenized form.
        var expectedCleanup = SeamCleanupLaunchesAt(PublicationSha);
        var trailing = fake.Launched[^expectedCleanup.Length..];
        for (var i = 0; i < trailing.Count; i++)
            Assert.Equal(expectedCleanup[i], trailing[i]);
    }

    /// <summary>
    /// FINALIZATION SEQUENCE (legacy): the same phase-aware command contract over the opaque
    /// route — the publication HEAD is resolved BEFORE the push, and the cleanup resets to the
    /// published SHA with EXACTLY one clean spelled <c>clean -fdx</c>.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_Finalization_AfterConfirmedPush_ResetsToThePublishedSha()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var headCalls = 0;
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                "diff --cached --name-only -z" => (0, StagedOutput("agents/coder.agents.md"), ""),
                // The first two HEAD probes (the preparation preflight and its post-restore
                // check) answer the BASELINE; the publication HEAD probe and the cleanup's
                // post-restore verification answer the DISTINCT PUBLICATION SHA.
                "rev-parse --verify HEAD^{commit}" =>
                    (0, (++headCalls <= 2
                        ? ConfigRepoPreparationFakes.BaselineSha
                        : PublicationSha) + "\n", ""),
                _ => null,
            },
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-fin-published", configRepoDir, git);

        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.True(result.GitStatus!.Pushed);
        Assert.Equal(4, headCalls); // preflight, post-restore, publication, cleanup-verify

        var expected = new List<string>(ConfigRepoPreparationFakes.LegacyCommands)
        {
            "add agents/*.agents.md",
            "diff --cached --name-only -z",
            "commit -m \"" + ImproverCommitMessage + "\"",
            "pull --no-rebase",
            "rev-parse --verify HEAD^{commit}", // the publication HEAD BEFORE the push
            "push",
        };
        expected.AddRange(LegacyCleanupCommandsAt(PublicationSha));
        Assert.Equal(expected, git.GitCommands);

        var cleans = git.GitCommands.Where(c => c.StartsWith("clean", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, cleans.Length);
        Assert.All(cleans, c => Assert.Equal("clean -fdx", c));
        Assert.DoesNotContain(git.GitCommands, c => c.Contains("-ffdx", StringComparison.Ordinal));
    }

    /// <summary>
    /// Installs a REAL <c>pre-push</c> hook that unconditionally rejects the push with git's
    /// own non-fast-forward refusal, so the publication genuinely fails without simulating
    /// the failure at a fake boundary.
    /// </summary>
    private static void InstallRejectingPrePushHook(RealGitPlayground playground, string why)
    {
        var worker = playground.WorkerDir;
        var hookPath = Path.Combine(worker, ".git", "hooks", "pre-push");
        File.WriteAllText(hookPath, $"#!/bin/sh\necho 'rejected: {why}' >&2\nexit 1\n".Replace("\r\n", "\n"));
        RealGitResult(worker, "update-index", "--chmod=+x", "--", ".git/hooks/pre-push");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>
    /// FINALIZATION WITHOUT PUBLICATION (both routes): a genuinely REJECTED push (a real git
    /// pre-push hook) is a truthful publication failure, and the step-end cleanup still
    /// restores the captured BASELINE — the residue the agent created during the run is gone
    /// from the verified end state.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Improver_Finalization_AgentFailure_RestoresTheCapturedBaseline(bool viaSeam)
    {
        using var playground = RealGitPlayground.Create(
            $"fin-baseline-{(viaSeam ? "seam" : "legacy")}", "REMOTE-BASELINE-V1\n");
        var worker = playground.WorkerDir;
        var baselineSha = playground.RemoteMainSha();
        InstallRejectingPrePushHook(playground, "finalization fixture");

        // The agent creates residue DURING the run: an edit to the tracked guidance file
        // (left UNCOMMITTED — the publication stage does the commit), an untracked file and
        // an ignored file.
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) =>
            {
                File.WriteAllText(playground.GuidancePath, "AGENT-DIRTY-EDIT\n");
                File.WriteAllText(Path.Combine(worker, "untracked-fin.txt"), "residue\n");
                File.WriteAllText(Path.Combine(worker, "ignored-fin.txt"), "ignored residue\n");
                return Task.FromResult("Mock agent response");
            },
        };

        var (result, _, _) = await RunRealGitImproverAsync(
            $"realgit-fin-baseline-{viaSeam}", playground, viaSeam, agentRunner);

        // The publication stage committed the edit, then the push was rejected → Failed.
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);

        // THE END STATE AT RETURN: restored to the captured fetched baseline, verified clean.
        Assert.Equal(baselineSha, RealGitOutput(worker, "rev-parse", "HEAD^{commit}").Trim());
        Assert.Equal("", playground.WorkerVerboseStatus());
        Assert.Equal("REMOTE-BASELINE-V1\n", File.ReadAllText(playground.GuidancePath));
        Assert.False(File.Exists(Path.Combine(worker, "untracked-fin.txt")));
        Assert.False(File.Exists(Path.Combine(worker, "ignored-fin.txt")));
        // The agent's output evidence and the failure reason survive composition.
        Assert.Contains("Mock agent response", result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Git Failure]", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// NEGATIVE CONTROL (a): removing the finalization call leaves the agent-created residue
    /// in place, so the end-state assertions fail. This test asserts the RESTORED (green)
    /// behavior — the verified-clean end state — and is the test that FAILS under the mutant.
    /// The mutant demonstration itself is a bash-driven build/test cycle: production is
    /// temporarily patched (the finalization call replaced by a bare passthrough), rebuilt,
    /// and this exact test re-run — the end-state assertion fails with residue still present —
    /// after which production is RESTORED and this test is green again.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NegativeControl_RemovedFinalization_LeavesResidue(bool viaSeam)
    {
        using var playground = RealGitPlayground.Create(
            $"negctrl-removed-finalization-{(viaSeam ? "seam" : "legacy")}", "REMOTE-BASELINE-V1\n");
        var worker = playground.WorkerDir;
        InstallRejectingPrePushHook(playground, "negative-control fixture");

        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) =>
            {
                File.WriteAllText(playground.GuidancePath, "CONTROL-DIRTY-EDIT\n");
                File.WriteAllText(Path.Combine(worker, "negctrl-residue.txt"), "residue\n");
                return Task.FromResult("Mock agent response");
            },
        };

        var (result, _, _) = await RunRealGitImproverAsync(
            "negctrl-probe", playground, viaSeam, agentRunner);

        // The push was genuinely rejected, so the outcome is Failed on both forms.
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.False(result.GitStatus!.Pushed);

        // THE END-STATE ASSERTION the mutant fails: the verified-clean tree at return.
        Assert.Equal("", playground.WorkerVerboseStatus());
        Assert.Equal(
            playground.RemoteMainSha(),
            RealGitOutput(worker, "rev-parse", "HEAD^{commit}").Trim());
        Assert.False(File.Exists(Path.Combine(worker, "negctrl-residue.txt")));
    }

    /// <summary>
    /// NEGATIVE CONTROL (b): rolling a confirmed success back to the BASELINE (instead of the
    /// published tip) fails the distinct published-tip check. This test asserts the RESTORED
    /// (green) behavior — the final HEAD equals the remote's published tip — and is the test
    /// that FAILS under the mutant (production temporarily patched so finalization always
    /// resets to the baseline; the mutant leaves the checkout behind the published tip).
    /// </summary>
    [Fact]
    public async Task NegativeControl_BaselineOnSuccess_FailsThePublishedTipCheck()
    {
        using var playground = RealGitPlayground.Create(
            "negctrl-baselineonsuccess", "REMOTE-BASELINE-V1\n");
        var worker = playground.WorkerDir;
        var baselineSha = playground.RemoteMainSha();

        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) =>
            {
                File.WriteAllText(playground.GuidancePath, "SUCCESS-EDIT\n");
                return Task.FromResult("Mock agent response");
            },
        };

        var (result, _, _) = await RunRealGitImproverAsync(
            "negctrl-baselineonsuccess", playground, viaSeam: true, agentRunner);

        // The push itself was confirmed.
        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.True(result.GitStatus!.Pushed);

        var remoteSha = playground.RemoteMainSha();
        Assert.NotEqual(baselineSha, remoteSha);

        // THE DISTINCT PUBLISHED-TIP CHECK the mutant fails: the final HEAD is the published
        // tip — NOT the older baseline.
        Assert.Equal(remoteSha, RealGitOutput(worker, "rev-parse", "HEAD^{commit}").Trim());
        Assert.Equal("SUCCESS-EDIT\n", RealGitOutput(worker, "show", "HEAD:agents/coder.agents.md"));
        Assert.Equal("", playground.WorkerVerboseStatus());
    }

    // ── Finalization failure vectors: never claim cleanliness unverified ──────

    /// <summary>
    /// A CLEANUP verification failure is composed ONTO the confirmed publication: the
    /// result stays truthful with <c>Pushed=true</c> and the agent evidence preserved, but a
    /// normal completion whose cleanup failed is reported Failed/FAIL with the sanitized
    /// cleanup reason appended — never Completed with an unverified "clean" claim.
    /// </summary>
    [Fact]
    public async Task Improver_ConfirmedPublication_PlusCleanupFailure_ReportsBoth()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var resetCalls = 0;
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                "diff --cached --name-only -z" => (0, StagedOutput("agents/coder.agents.md"), ""),
                _ => null,
            },
            // Only the SECOND reset (the cleanup's) throws; the preparation's succeeds.
            GitCommandThrower = args => args.StartsWith("reset --hard", StringComparison.Ordinal)
                && ++resetCalls == 2
                ? new InvalidOperationException("cleanup reset exploded")
                : null,
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-fin-cleanup-fail", configRepoDir, git);

        // TRUTHFUL COMPOSITION: the push was confirmed (Pushed=true survives) but the
        // normal completion was downgraded to Failed/FAIL because cleanup could not verify.
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.True(result.GitStatus!.Pushed);
        // The agent evidence AND the original publication evidence survive, with the cleanup
        // diagnostics APPENDED.
        Assert.StartsWith("Mock agent response", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("[Config Repo Git Failure]", result.Output);
        Assert.Contains("[Config Repo Cleanup Failure]", result.Output, StringComparison.Ordinal);
        Assert.Contains(
            result.Metrics.Issues,
            i => i.Contains("Config repo cleanup reset failed", StringComparison.Ordinal)
                && !i.Contains("exploded", StringComparison.Ordinal));
    }

    /// <summary>Counts the reset commands issued so far on the legacy mock.</summary>
    private static int CountResets(MockGitOperations git) =>
        git.GitCommands.Count(c => c.StartsWith("reset ", StringComparison.Ordinal));

    /// <summary>
    /// THE COMMON FINALIZED-RESULT RETURN BOUNDARY carries the ORIGINAL ASSIGNED model even when
    /// the Improver finalization ADJUSTED the outcome: here a normal completion is downgraded to
    /// Failed/FAIL by a cleanup failure, so the model must survive the composition rather than
    /// only being stamped on a plain Completed result.
    /// <para>
    /// The value is asserted VERBATIM (leading/trailing whitespace and a provider prefix
    /// included), proving the producer neither trims, normalizes, nor substitutes a display or
    /// environment value. This reuses the existing cleanup-failure fake seam rather than
    /// re-running the wider cleanup matrix.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ImproverCleanupAdjustedOutcome_CarriesAssignedModelVerbatim()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        const string assignedModel = "  copilot/claude-sonnet-4.6  ";
        var resetCalls = 0;
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                "diff --cached --name-only -z" => (0, StagedOutput("agents/coder.agents.md"), ""),
                _ => null,
            },
            // Only the SECOND reset (the cleanup's) throws; the preparation's succeeds.
            GitCommandThrower = args => args.StartsWith("reset --hard", StringComparison.Ordinal)
                && ++resetCalls == 2
                ? new InvalidOperationException("cleanup reset exploded")
                : null,
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-model-after-cleanup", configRepoDir, git, model: assignedModel);

        // The outcome really was ADJUSTED by the finalization — this is the composed path.
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Contains("[Config Repo Cleanup Failure]", result.Output, StringComparison.Ordinal);

        // …and the assigned model came through the adjustment untouched.
        Assert.Equal(assignedModel, result.Model);
    }

    /// <summary>
    /// The model is populated at the COMMON boundary for a NON-Improver task as well — the
    /// ordinary Completed return path — and a whitespace-only assigned model passes through
    /// verbatim (never collapsed to empty, never trimmed).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhitespaceAssignedModel_PassesThroughVerbatim()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                "diff --cached --name-only -z" => (0, StagedOutput("agents/coder.agents.md"), ""),
                _ => null,
            },
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-blank-model", configRepoDir, git, model: "   ");

        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Equal("   ", result.Model);
    }

    /// <summary>
    /// A runtime-NULL assigned model is represented as the domain empty string — the "unknown"
    /// form — and never as a placeholder.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NullAssignedModel_IsRepresentedAsEmpty()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                "diff --cached --name-only -z" => (0, StagedOutput("agents/coder.agents.md"), ""),
                _ => null,
            },
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-null-model", configRepoDir, git, model: null!);

        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Equal("", result.Model);
    }

    /// <summary>
    /// A CLEANUP trust rejection — here a nonempty post-restore status (the protected
    /// nested-repo shape) — is a truthful cleanup failure on a confirmed publication: Failed
    /// with <c>Pushed=true</c> retained, and NO second forced clean on the cleanup path.
    /// </summary>
    [Fact]
    public async Task Improver_CleanupNonEmptyStatus_IsATruthfulCleanupFailure()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var statusCalls = 0;
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                "diff --cached --name-only -z" => (0, StagedOutput("agents/coder.agents.md"), ""),
                "status --porcelain=v1 --untracked-files=all --ignored" =>
                    (0, (++statusCalls == 1 ? "" : "?? nested/\n"), ""),
                _ => null,
            },
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-fin-dirty-status", configRepoDir, git);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.True(result.GitStatus!.Pushed);
        Assert.Contains(
            result.Metrics.Issues,
            i => i.Contains("Config repo cleanup rejected: the working tree is not clean", StringComparison.Ordinal));
        Assert.Contains("[Config Repo Cleanup Failure]", result.Output, StringComparison.Ordinal);
        // Exactly TWO cleans (preparation + cleanup), never `-ffdx`: no escalation.
        var cleans = git.GitCommands.Where(c => c.StartsWith("clean", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, cleans.Length);
        Assert.All(cleans, c => Assert.Equal("clean -fdx", c));
        Assert.DoesNotContain(git.GitCommands, c => c.Contains("-ffdx", StringComparison.Ordinal));
    }

    /// <summary>
    /// A CLEANUP verification probe with MALFORMED output (a padded HEAD SHA) is a truthful
    /// cleanup failure: the HEAD equality can never be verified, so the normal completion is
    /// failed with the sanitized cleanup diagnostics and the confirmed Pushed=true retained.
    /// </summary>
    [Fact]
    public async Task Improver_CleanupMalformedHeadVerification_IsATruthfulCleanupFailure()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var headCalls = 0;
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                "diff --cached --name-only -z" => (0, StagedOutput("agents/coder.agents.md"), ""),
                // The CLEANUP's post-restore HEAD probe (4th) is PADDED — malformed.
                "rev-parse --verify HEAD^{commit}" => (0, (++headCalls == 4
                    ? " " + ConfigRepoPreparationFakes.BaselineSha + " \n"
                    : ConfigRepoPreparationFakes.BaselineSha + "\n"), ""),
                _ => null,
            },
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-fin-malformed-head", configRepoDir, git);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.True(result.GitStatus!.Pushed);
        Assert.Contains(
            result.Metrics.Issues,
            i => i.Contains("Config repo cleanup rejected: HEAD did not resolve to a single full commit SHA", StringComparison.Ordinal));
    }

    /// <summary>
    /// A CLEANUP stage COMMAND failure (a non-zero reset) is a truthful cleanup failure, and
    /// the sequence STOPS: no clean, no verification, and no publication follow the failed
    /// cleanup reset.
    /// </summary>
    [Fact]
    public async Task Improver_CleanupFailedReset_StopsTheCleanupSequence()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var resetCalls = 0;
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                "diff --cached --name-only -z" => (0, StagedOutput("agents/coder.agents.md"), ""),
                // The CLEANUP's reset (the 2nd) fails with a non-zero exit.
                var r when r.StartsWith("reset ", StringComparison.Ordinal) =>
                    (++resetCalls == 2 ? (128, "", "fatal: cleanup reset refused") : (0, "", "")),
                _ => null,
            },
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-fin-reset-fail", configRepoDir, git);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.True(result.GitStatus!.Pushed);
        Assert.Contains(
            result.Metrics.Issues,
            i => i.Contains("Config repo cleanup reset failed (exit 128)", StringComparison.Ordinal));
        // The failed cleanup reset stopped the cleanup sequence: only the PREPARATION clean
        // and the PREPARATION status ran (the cleanup's clean and status never launched).
        Assert.Single(git.GitCommands, c => c.StartsWith("clean", StringComparison.Ordinal));
        Assert.Single(git.GitCommands, c => c.StartsWith("status", StringComparison.Ordinal));
    }

    /// <summary>
    /// A cleanup stage that THROWS (an ordinary exception) is a sanitized cleanup failure —
    /// the exception classification only, never the raw message.
    /// </summary>
    [Fact]
    public async Task Improver_CleanupThrownCommand_IsSanitizedCleanupFailure()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var cleanCalls = 0;
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                "diff --cached --name-only -z" => (0, StagedOutput("agents/coder.agents.md"), ""),
                _ => null,
            },
            GitCommandThrower = args => args.StartsWith("clean", StringComparison.Ordinal)
                && ++cleanCalls == 2
                ? new InvalidOperationException("cleanup clean exploded")
                : null,
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-fin-clean-throw", configRepoDir, git);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.True(result.GitStatus!.Pushed);
        Assert.Contains(
            result.Metrics.Issues,
            i => i.Contains("Config repo cleanup clean failed with an error [InvalidOperationException]", StringComparison.Ordinal)
                && !i.Contains("exploded", StringComparison.Ordinal));
    }

    /// <summary>
    /// (a) A GENUINELY REQUESTED execution cancellation — raised after the agent prompt has
    /// returned — keeps Cancelled/CANCELLED, and the step-end cleanup STILL runs afterwards on
    /// its OWN independent budget token rather than the cancelled execution token.
    /// <para>
    /// The previous form of this test never cancelled its CTS and asserted a Failed/FAIL
    /// outcome after a nonzero pull, so it proved only that two token values differ — it could
    /// not fail if cleanup started honouring the cancelled token, because that token was never
    /// cancelled. Here the execution token is REALLY cancelled inside the publication stage, so
    /// a cleanup that used it would be pre-cancelled and could not issue a single command.
    /// </para>
    /// <para>
    /// REMOVAL-PROOF on three axes: (1) the Cancelled/CANCELLED classification, (2) the cleanup
    /// commands actually issued AFTER the cancellation (a cleanup on the execution token issues
    /// none), and (3) the explicit cleanup-failure report on the cancellation path.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Improver_CleanupRunsOnItsOwnBudget_AfterExecutionCancellation()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        using var cts = new CancellationTokenSource();
        const string agentSegment = "Improver analysis completed before the shutdown signal";
        var statusCalls = 0;
        var git = new MockGitOperations
        {
            GitCommandResponder = args =>
            {
                // The publication's FIRST command REQUESTS the execution cancellation — after
                // the agent prompt already returned real output.
                if (args == "add agents/*.agents.md")
                {
                    cts.Cancel();
                    throw new OperationCanceledException("shutdown", cts.Token);
                }

                // The CLEANUP's status (the 2nd overall) reports residue, so the cancellation
                // path must ALSO report the cleanup failure explicitly.
                if (args == "status --porcelain=v1 --untracked-files=all --ignored")
                    return (0, ++statusCalls == 1 ? "" : "?? residue.txt\n", "");

                return null;
            },
        };
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) => Task.FromResult(agentSegment),
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-fin-cancelled", configRepoDir, git, cts.Token, agentRunner);

        // NON-VACUITY: the execution token really WAS cancelled, and the agent really ran
        // before it happened.
        Assert.True(cts.IsCancellationRequested, "the execution token was never cancelled — the test is vacuous");
        Assert.Single(agentRunner.PromptCalls);

        // (1) CANCELLED SEMANTICS PRESERVED — never reclassified into an ordinary failure.
        Assert.Equal(TaskOutcome.Cancelled, result.Status);
        Assert.Equal("CANCELLED", result.Metrics!.Verdict);

        // (2) THE CLEANUP RAN ON ITS OWN BUDGET. Every cleanup command was issued AFTER the
        // cancellation was requested; a cleanup that used the (now cancelled) execution token
        // would have been pre-cancelled and issued NOTHING.
        var addIndex = git.GitCommands.IndexOf("add agents/*.agents.md");
        Assert.True(addIndex >= 0, "the publication stage never started");
        var afterCancellation = git.GitCommands.Skip(addIndex + 1).ToArray();
        Assert.Equal(ConfigRepoPreparationFakes.LegacyCleanupCommands, afterCancellation);

        // The cleanup's own token is NOT the cancelled execution token, and is not already
        // cancelled when the commands are issued.
        for (var i = addIndex + 1; i < git.GitTokens.Count; i++)
        {
            Assert.NotEqual(cts.Token, git.GitTokens[i]);
            Assert.False(
                git.GitTokens[i].IsCancellationRequested,
                "the cleanup command carried an already-cancelled token");
        }

        // (3) THE CLEANUP FAILURE IS REPORTED EXPLICITLY on the cancellation path, and the
        // agent's accumulated evidence survives alongside it.
        Assert.Contains(agentSegment, result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Cleanup Failure]", result.Output, StringComparison.Ordinal);
        Assert.Contains(
            result.Metrics.Issues,
            i => i.Contains("Config repo cleanup rejected", StringComparison.Ordinal));
    }

    /// <summary>
    /// (a, seam converse) The same genuinely-cancelled vector on the TOKENIZED seam route, with
    /// a cleanup that SUCCEEDS: Cancelled/CANCELLED is preserved and the cleanup still issues
    /// its full tokenized sequence on the independent budget token.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_CleanupRunsOnItsOwnBudget_AfterExecutionCancellation()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        using var cts = new CancellationTokenSource();
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens =>
            {
                if (tokens is ["add", ..])
                {
                    cts.Cancel();
                    throw new OperationCanceledException("shutdown", cts.Token);
                }

                return null;
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-fin-cancelled", configRepoDir, seam, fake, git, cts.Token, agentRunner);

        Assert.True(cts.IsCancellationRequested, "the execution token was never cancelled — the test is vacuous");
        Assert.Single(agentRunner.PromptCalls);

        // Cancelled semantics preserved; a SUCCESSFUL cleanup adds no failure diagnostics.
        Assert.Equal(TaskOutcome.Cancelled, result.Status);
        Assert.Equal("CANCELLED", result.Metrics!.Verdict);
        Assert.DoesNotContain("[Config Repo Cleanup Failure]", result.Output, StringComparison.Ordinal);

        // The cleanup issued its FULL tokenized sequence after the cancellation.
        var addIndex = fake.Launched.FindIndex(t => t is ["add", ..]);
        Assert.True(addIndex >= 0, "the publication stage never started");
        var afterCancellation = fake.Launched.Skip(addIndex + 1).ToArray();
        Assert.Equal(ConfigRepoPreparationFakes.SeamCleanupLaunches.Length, afterCancellation.Length);
        for (var i = 0; i < afterCancellation.Length; i++)
            Assert.Equal(ConfigRepoPreparationFakes.SeamCleanupLaunches[i], afterCancellation[i]);

        // Every post-cancellation launch used a live, independent token.
        for (var i = addIndex + 1; i < fake.Tokens.Count; i++)
        {
            Assert.NotEqual(cts.Token, fake.Tokens[i]);
            Assert.False(
                fake.Tokens[i].IsCancellationRequested,
                "the cleanup launch carried an already-cancelled token");
        }
    }

    /// <summary>
    /// (b) PARTIAL PREPARATION INTERRUPTED BY A REAL REQUESTED CANCELLATION, AFTER TARGET
    /// CAPTURE. The preflight, the fetch and the FETCH_HEAD validation all succeed — so the
    /// trusted restore target is already captured — and the destructive restore's reset is then
    /// interrupted by a genuinely requested execution cancellation.
    /// <para>
    /// The previous form injected a NONZERO reset (an ordinary command failure), which never
    /// exercised the cancellation path this test claims to cover. Here the execution token is
    /// really cancelled mid-preparation, so the outcome is Cancelled/CANCELLED and finalization
    /// must still complete the restore TOWARD THE CAPTURED TARGET on its independent budget.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Improver_PartialPreparationCancelledAfterTargetCapture_IsFinalizedToTheTarget()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        using var cts = new CancellationTokenSource();
        var resetCalls = 0;
        var git = new MockGitOperations
        {
            GitCommandResponder = args =>
            {
                // The PREPARATION's reset (the 1st) is interrupted by a REQUESTED cancellation,
                // after the fetch + FETCH_HEAD validation already captured the target.
                if (args.StartsWith("reset ", StringComparison.Ordinal) && ++resetCalls == 1)
                {
                    cts.Cancel();
                    throw new OperationCanceledException("shutdown", cts.Token);
                }

                return null;
            },
        };
        var agentRunner = new MockAgentRunner();

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-partial-prep-cancelled", configRepoDir, git, cts.Token, agentRunner);

        // NON-VACUITY: a REAL requested cancellation, raised after target capture.
        Assert.True(cts.IsCancellationRequested, "the execution token was never cancelled — the test is vacuous");
        // The agent was never prompted: preparation was interrupted before the prompt.
        Assert.Empty(agentRunner.PromptCalls);

        // The requested cancellation keeps its established semantics.
        Assert.Equal(TaskOutcome.Cancelled, result.Status);
        Assert.Equal("CANCELLED", result.Metrics!.Verdict);

        // FINALIZATION COMPLETED THE RESTORE TOWARD THE CAPTURED TARGET: the cleanup issued its
        // full sequence, resetting to the captured fetched baseline (never a guess), after the
        // interrupted preparation.
        string[] preparationPrefix =
        [
            "rev-parse --show-toplevel",
            "rev-parse --verify HEAD^{commit}",
            "rev-parse --symbolic-full-name HEAD",
            "rev-parse --symbolic-full-name @{upstream}",
            $"fetch origin \"{ConfigRepoPreparationFakes.BranchRef}\"",
            "rev-parse --verify FETCH_HEAD^{commit}",
            $"reset --hard {ConfigRepoPreparationFakes.BaselineSha}",
        ];
        Assert.Equal(
            [.. preparationPrefix, .. ConfigRepoPreparationFakes.LegacyCleanupCommands],
            git.GitCommands);

        // The cleanup's own commands ran on a LIVE, independent token after the interruption.
        var interruptedIndex = preparationPrefix.Length - 1;
        for (var i = interruptedIndex + 1; i < git.GitTokens.Count; i++)
        {
            Assert.NotEqual(cts.Token, git.GitTokens[i]);
            Assert.False(
                git.GitTokens[i].IsCancellationRequested,
                "the cleanup command carried an already-cancelled token");
        }

        // The cleanup SUCCEEDED, so no cleanup-failure diagnostics are reported.
        Assert.DoesNotContain("[Config Repo Cleanup Failure]", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// (b, ordinary-failure converse) The pre-existing partial-preparation vector, kept as the
    /// NON-cancellation cell: a nonzero preparation reset after target capture is an ordinary
    /// Failed/FAIL outcome, and finalization still completes the restore to the captured target.
    /// </summary>
    [Fact]
    public async Task Improver_PartialPreparationAfterTargetCapture_IsFinalizedToTheTarget()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var resetCalls = 0;
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                // The PREPARATION's reset (the 1st) fails after the fetch + SHA validation
                // succeeded — a partial preparation with the target ALREADY captured.
                var r when r.StartsWith("reset ", StringComparison.Ordinal) =>
                    (++resetCalls == 1 ? (128, "", "fatal: partial restore refused") : (0, "", "")),
                _ => null,
            },
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-partial-prep", configRepoDir, git);

        // The preparation failure is truthful, and the CLEANUP completed the restore to the
        // captured baseline afterwards.
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        // The cleanup's reset (the 2nd) ran to the SAME captured target and its verification
        // probes followed. (The PREPARATION's clean never ran — it follows the failed reset —
        // so exactly ONE clean exists, the cleanup's.)
        Assert.Equal(2, git.GitCommands.Count(c => c.StartsWith("reset ", StringComparison.Ordinal)));
        Assert.Equal(1, git.GitCommands.Count(c => c.StartsWith("clean", StringComparison.Ordinal)));
        // The preflight HEAD probe + the cleanup's post-restore verification probe (the
        // preparation's own post-restore probe never ran after the failed reset).
        Assert.Equal(2, git.GitCommands.Count(c => c == "rev-parse --verify HEAD^{commit}"));
        Assert.Single(git.GitCommands, c => c.StartsWith("status", StringComparison.Ordinal));
        // The captured baseline SHA was the CLEANUP's reset target (never a guess).
        Assert.Contains($"reset --hard {ConfigRepoPreparationFakes.BaselineSha}", git.GitCommands);
        // The preparation failure evidence is preserved; the cleanup diagnostics are absent
        // because the cleanup SUCCEEDED.
        Assert.DoesNotContain("[Config Repo Cleanup Failure]", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// REAL GIT: the step-end cleanup after a REJECTED push removes every residue class the
    /// agent created during the run, restores the captured baseline, and leaves the OUTSIDE
    /// sentinel and the protected nested repository untouched (the nested repo's residual
    /// status is a truthful cleanup FAILURE, never a forced deletion).
    /// </summary>
    [Fact]
    public async Task RealGit_Finalization_ProtectedNestedRepo_IsReportedAndNeverDeleted()
    {
        using var playground = RealGitPlayground.Create(
            "fin-nested-protected", "REMOTE-BASELINE-V1\n");
        var worker = playground.WorkerDir;
        var baselineSha = playground.RemoteMainSha();
        InstallRejectingPrePushHook(playground, "finalization fixture");

        // A NESTED repository with its own committed content, created DURING the agent run.
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) =>
            {
                File.WriteAllText(playground.GuidancePath, "AGENT-DIRTY-EDIT\n");
                File.WriteAllText(Path.Combine(worker, "untracked-ordinary.txt"), "ordinary\n");
                var nestedDir = Path.Combine(worker, "nested-repo");
                Directory.CreateDirectory(nestedDir);
                RealGit(nestedDir, "init", "-b", "main");
                File.WriteAllText(Path.Combine(nestedDir, "nested-content.txt"), "nested-precious\n");
                RealGit(nestedDir, "add", "-A");
                return Task.FromResult("Mock agent response");
            },
        };

        var (result, _, _) = await RunRealGitImproverAsync(
            "realgit-fin-nested", playground, viaSeam: false, agentRunner);

        // The push was rejected → Failed, AND the cleanup could not verify cleanliness
        // because the protected nested repository leaves residual status — a truthful
        // cleanup failure composed onto the outcome.
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.False(result.GitStatus is null ? true : result.GitStatus.Pushed);
        Assert.NotNull(result.Metrics);
        Assert.Contains(
            result.Metrics!.Issues,
            i => i.Contains("Config repo cleanup", StringComparison.Ordinal));
        Assert.Contains("[Config Repo Cleanup Failure]", result.Output, StringComparison.Ordinal);

        // THE PROTECTED NESTED REPOSITORY SURVIVED, intact.
        var nested = Path.Combine(worker, "nested-repo");
        Assert.True(Directory.Exists(nested));
        Assert.True(Directory.Exists(Path.Combine(nested, ".git")));
        Assert.True(File.Exists(Path.Combine(nested, "nested-content.txt")));
        Assert.Equal("nested-precious\n", File.ReadAllText(Path.Combine(nested, "nested-content.txt")));

        // The ordinary residue WAS removed by the cleanup's clean before the nested-repo
        // status failure.
        Assert.False(File.Exists(Path.Combine(worker, "untracked-ordinary.txt")));

        // THE OUTSIDE SENTINEL is untouched, byte for byte.
        Assert.True(File.Exists(playground.OutsideSentinelPath));
        Assert.Equal("outside-untouched\n", File.ReadAllText(playground.OutsideSentinelPath));

        // The reset target was the captured baseline, not HEAD-at-entry.
        var reflog = RealGitOutput(worker, "reflog", "--format=%gs");
        Assert.Contains("reset", reflog, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ITERATION 2 — regression tests for the four production defects
    // ══════════════════════════════════════════════════════════════════════════

    // ── MAJOR-1: a publication-HEAD failure must keep the agent/Git evidence ──

    /// <summary>
    /// MAJOR-1 (legacy route). The publication-HEAD probe runs after the successful pull and
    /// before the push. When it fails, its stage helper THROWS a ConfigRepoPublicationException
    /// carrying an EMPTY preservedOutput and no summary — so letting it escape bypasses the
    /// caller's wrapper and returns empty agent evidence plus empty Git diagnostics.
    /// <para>
    /// REMOVAL-PROOF: the failure must be converted into the ordinary publication-failure shape
    /// so the wrapper attaches BOTH the accumulated agent output AND the staged
    /// <see cref="GitChangeSummary"/>. Reintroducing the defect (removing the catch around the
    /// publication-HEAD probe) empties Output and ChangedFiles and fails every assertion below.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(128, "", "fatal: publication HEAD refused")]      // nonzero exit
    [InlineData(0, "  not-a-sha  \n", "")]                        // malformed (padded) output
    [InlineData(0, "", "")]                                       // exit-zero, empty output
    public async Task Improver_LegacyPath_PublicationHeadFailure_PreservesAgentAndGitEvidence(
        int exitCode, string stdout, string stderr)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        string[] staged = ["agents/coder.agents.md", "agents/tester.agents.md"];
        var headCalls = 0;
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                "diff --cached --name-only -z" => (0, StagedOutput(staged), ""),
                // The preparation's two HEAD probes succeed; the PUBLICATION HEAD probe (3rd)
                // fails in the shape under test.
                "rev-parse --verify HEAD^{commit}" => ++headCalls == 3
                    ? (exitCode, stdout, stderr)
                    : (0, ConfigRepoPreparationFakes.BaselineSha + "\n", ""),
                _ => null,
            },
        };
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) => Task.FromResult("Improver analysis of the guidance files"),
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            $"improver-legacy-pubhead-{exitCode}-{stdout.Length}", configRepoDir, git,
            agentRunner: agentRunner);

        // Truthful publication failure.
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);

        // THE AGENT EVIDENCE SURVIVES — never an empty output.
        Assert.StartsWith("Improver analysis of the guidance files", result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Git Failure]", result.Output, StringComparison.Ordinal);

        // THE GIT DIAGNOSTICS SURVIVE — the staged summary reaches the orchestrator.
        Assert.Equal(2, result.GitStatus.FilesChanged);
        Assert.Equal(staged, result.GitStatus.ChangedFiles);

        // The sanitized publication-stage reason is reported.
        Assert.Contains(
            result.Metrics.Issues,
            i => i.Contains("Config repo publication", StringComparison.Ordinal));

        // PUSH STAYS BLOCKED after the publication-HEAD failure.
        Assert.DoesNotContain(git.GitCommands, c => c == "push");
    }

    /// <summary>
    /// MAJOR-1 (seam route): the converse cell. A publication-HEAD probe that the seam cannot
    /// even launch (a throwing command mapped to the seam's exit -1 rejection) must equally
    /// preserve the accumulated agent output and the staged summary, and must not push.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_PublicationHeadThrow_PreservesAgentAndGitEvidence()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var headCalls = 0;
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens switch
            {
                ["diff", ..] => new GitProcessResult(0, StagedOutput("agents/coder.agents.md"), ""),
                ["rev-parse", "--verify", "HEAD^{commit}"] => ++headCalls == 3
                    ? throw new InvalidOperationException("publication head probe exploded")
                    : new GitProcessResult(0, ConfigRepoPreparationFakes.BaselineSha + "\n", ""),
                _ => null,
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) => Task.FromResult("Seam improver analysis"),
        };

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-pubhead-throw", configRepoDir, seam, fake, git, agentRunner: agentRunner);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);

        // Evidence preserved on BOTH axes.
        Assert.StartsWith("Seam improver analysis", result.Output, StringComparison.Ordinal);
        Assert.Equal(["agents/coder.agents.md"], result.GitStatus.ChangedFiles);
        Assert.Equal(1, result.GitStatus.FilesChanged);

        // SANITIZED: the seam's fixed rejection classification, never the raw message.
        Assert.Contains(
            result.Metrics.Issues,
            i => i.Contains("Config repo publication", StringComparison.Ordinal)
                && !i.Contains("exploded", StringComparison.Ordinal));

        // Push never launched.
        Assert.DoesNotContain(fake.Launched, t => t is ["push", ..]);
    }

    /// <summary>
    /// (c) The REMAINING publication-HEAD cells, completing the matrix over both routes and
    /// both failure forms. Round 1 covered the legacy NONZERO/MALFORMED/EMPTY probe and the
    /// seam THROW; these are the converse cells — the legacy THROW and the seam
    /// NONZERO/MALFORMED probe.
    /// <para>
    /// Every cell asserts the SAME contract: the accumulated agent output and the staged
    /// <see cref="GitChangeSummary"/> both survive (never empty evidence), the reason is the
    /// sanitized publication-stage classification, and push stays blocked.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_PublicationHeadThrow_PreservesAgentAndGitEvidence()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        string[] staged = ["agents/coder.agents.md", "agents/reviewer.agents.md"];
        var headCalls = 0;
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args == "diff --cached --name-only -z"
                ? (0, StagedOutput(staged), "")
                : null,
            // The PUBLICATION HEAD probe (the 3rd HEAD probe) THROWS an ordinary exception.
            GitCommandThrower = args => args == "rev-parse --verify HEAD^{commit}" && ++headCalls == 3
                ? new InvalidOperationException("legacy publication head probe exploded")
                : null,
        };
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) => Task.FromResult("Legacy improver analysis segment"),
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-legacy-pubhead-throw", configRepoDir, git, agentRunner: agentRunner);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);

        // EVIDENCE PRESERVED on both axes — never an empty output, never empty diagnostics.
        Assert.StartsWith("Legacy improver analysis segment", result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Git Failure]", result.Output, StringComparison.Ordinal);
        Assert.Equal(2, result.GitStatus.FilesChanged);
        Assert.Equal(staged, result.GitStatus.ChangedFiles);

        // SANITIZED: the classification only — the raw exception message never escapes.
        Assert.Contains(
            result.Metrics.Issues,
            i => i.Contains("Config repo publication", StringComparison.Ordinal)
                && i.Contains("InvalidOperationException", StringComparison.Ordinal)
                && !i.Contains("exploded", StringComparison.Ordinal));

        // PUSH STAYS BLOCKED.
        Assert.DoesNotContain(git.GitCommands, c => c == "push");
    }

    /// <summary>
    /// (c) The seam converse of the nonzero/malformed publication-HEAD probe.
    /// </summary>
    [Theory]
    [InlineData(128, "", "fatal: publication HEAD refused")]  // nonzero exit
    [InlineData(0, "  not-a-sha  \n", "")]                    // malformed (padded) output
    [InlineData(0, "", "")]                                   // exit-zero, empty output
    public async Task Improver_SeamPath_PublicationHeadNonzeroOrMalformed_PreservesEvidence(
        int exitCode, string stdout, string stderr)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var headCalls = 0;
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens switch
            {
                ["diff", ..] => new GitProcessResult(0, StagedOutput("agents/coder.agents.md"), ""),
                // The PUBLICATION HEAD probe (the 3rd) fails in the shape under test.
                ["rev-parse", "--verify", "HEAD^{commit}"] => ++headCalls == 3
                    ? new GitProcessResult(exitCode, stdout, stderr)
                    : new GitProcessResult(0, ConfigRepoPreparationFakes.BaselineSha + "\n", ""),
                _ => null,
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) => Task.FromResult("Seam improver analysis segment"),
        };

        var (result, _, _) = await RunImproverWithSeamAsync(
            $"improver-seam-pubhead-{exitCode}-{stdout.Length}", configRepoDir, seam, fake, git,
            agentRunner: agentRunner);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);

        // EVIDENCE PRESERVED on both axes.
        Assert.StartsWith("Seam improver analysis segment", result.Output, StringComparison.Ordinal);
        Assert.Contains("[Config Repo Git Failure]", result.Output, StringComparison.Ordinal);
        Assert.Equal(1, result.GitStatus.FilesChanged);
        Assert.Equal(["agents/coder.agents.md"], result.GitStatus.ChangedFiles);

        // The sanitized publication-stage reason is reported.
        Assert.Contains(
            result.Metrics.Issues,
            i => i.Contains("Config repo publication", StringComparison.Ordinal));

        // PUSH STAYS BLOCKED after the publication-HEAD failure.
        Assert.DoesNotContain(fake.Launched, t => t is ["push", ..]);
    }

    // ── MAJOR-2: confirmed publication survives a throwing post-push log ──────

    /// <summary>
    /// A <see cref="TextWriter"/> that throws on the FIRST write matching a needle, then
    /// behaves normally. It models a disposed/failing console writer at one precise point —
    /// exactly what <see cref="WorkerLogger"/> (which writes straight to Console) exposes the
    /// production code to.
    /// </summary>
    private sealed class ThrowOnNeedleWriter(string needle) : TextWriter
    {
        private readonly StringWriter _inner = new();
        private bool _fired;

        public override System.Text.Encoding Encoding => _inner.Encoding;

        /// <summary>Whether the injected failure actually fired (guards against a vacuous test).</summary>
        public bool Fired => _fired;

        public override void WriteLine(string? value)
        {
            if (!_fired && value is not null && value.Contains(needle, StringComparison.Ordinal))
            {
                _fired = true;
                throw new IOException($"simulated writer failure on '{needle}'");
            }

            _inner.WriteLine(value);
        }

        public override void Write(char value) => _inner.Write(value);

        public override string ToString() => _inner.ToString();
    }

    /// <summary>
    /// MAJOR-2. After a CONFIRMED exit-zero push the production code logs
    /// <c>Pushed config repo changes …</c> through <see cref="WorkerLogger.Info"/>, which writes
    /// straight to <see cref="Console.Out"/>. A failing writer therefore throws into the generic
    /// handler AFTER the publication was confirmed.
    /// <para>
    /// REMOVAL-PROOF: both publication facts (the published SHA and the <c>Pushed=true</c>
    /// summary) must be retained BEFORE that fallible log, and every later outcome constructed
    /// from the retained evidence. Reintroducing the defect (materializing <c>Pushed=true</c>
    /// only at the return statement) yields a result whose GitStatus is null/unpushed even
    /// though finalization reset to the published SHA — failing the assertions below.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Improver_ConfirmedPush_ThrowingPostPushLog_StillReportsPushed()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        string[] staged = ["agents/coder.agents.md"];
        var headCalls = 0;
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                "diff --cached --name-only -z" => (0, StagedOutput(staged), ""),
                "rev-parse --verify HEAD^{commit}" => (0, (++headCalls <= 2
                    ? ConfigRepoPreparationFakes.BaselineSha
                    : PublicationSha) + "\n", ""),
                _ => null,
            },
        };

        var originalOut = Console.Out;
        var originalErr = Console.Error;
        // The FIRST post-push info line is the one that throws.
        var throwingOut = new ThrowOnNeedleWriter("Pushed config repo changes");
        using var errWriter = new StringWriter();
        TaskResult result;
        try
        {
            Console.SetOut(throwingOut);
            Console.SetError(errWriter);

            var executor = new TaskExecutor(
                new MockAgentRunner(), gitOperations: git, configRepoDir: configRepoDir);
            result = await executor.ExecuteAsync(
                BuildImproverTask("improver-pushlog-throws"), TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }

        // NON-VACUITY: the injected failure really fired on the post-push log line.
        Assert.True(throwingOut.Fired, "the post-push log write did not throw — the test is vacuous");

        // THE CONFIRMED PUBLICATION SURVIVES the throwing log.
        Assert.NotNull(result.GitStatus);
        Assert.True(result.GitStatus!.Pushed, "Pushed=true was lost when the post-push log threw");
        Assert.Equal(staged, result.GitStatus.ChangedFiles);

        // And the step-end cleanup used the PUBLISHED SHA (the retained selection), proving the
        // two facts were retained together.
        Assert.Contains($"reset --hard {PublicationSha}", git.GitCommands);
        Assert.DoesNotContain(
            git.GitCommands,
            c => c == $"reset --hard {ConfigRepoPreparationFakes.BaselineSha}"
                && git.GitCommands.IndexOf(c) > git.GitCommands.IndexOf("push"));
    }

    // ── MAJOR-3: finalization is exception-safe against its own diagnostics ───

    /// <summary>
    /// MAJOR-3 (successful cleanup). The verified-clean path logs
    /// <c>Config repo cleanup verified …</c>. Through <see cref="WorkerLogger.Info"/> that write
    /// goes straight to <see cref="Console.Out"/>, so a failing writer would escape
    /// <c>FinalizeImproverOutcomeAsync</c> and hide the primary result — or turn a VERIFIED
    /// cleanup into a cleanup failure.
    /// <para>
    /// REMOVAL-PROOF: the diagnostic must go through a nonthrowing boundary. Reintroducing the
    /// defect (calling <c>_log.Info</c>/<c>_log.Error</c> directly) makes the throw escape and
    /// this test fail — the result is no longer the primary Completed outcome.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Improver_ThrowingCleanupSuccessLog_StillReturnsThePrimaryResult()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args == "diff --cached --name-only -z"
                ? (0, "", "") // a genuine no-change completion
                : null,
        };

        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var throwingOut = new ThrowOnNeedleWriter("Config repo cleanup verified");
        using var errWriter = new StringWriter();
        TaskResult result;
        try
        {
            Console.SetOut(throwingOut);
            Console.SetError(errWriter);

            var executor = new TaskExecutor(
                new MockAgentRunner(), gitOperations: git, configRepoDir: configRepoDir);
            result = await executor.ExecuteAsync(
                BuildImproverTask("improver-cleanuplog-throws"), TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }

        Assert.True(throwingOut.Fired, "the cleanup success log did not throw — the test is vacuous");

        // THE PRIMARY RESULT SURVIVES: a verified cleanup stays a normal completion — the
        // failing diagnostic neither escapes nor manufactures a cleanup failure.
        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Equal("PASS", result.Metrics!.Verdict);
        Assert.DoesNotContain("[Config Repo Cleanup Failure]", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(
            result.Metrics.Issues,
            i => i.Contains("Config repo cleanup", StringComparison.Ordinal));
    }

    /// <summary>
    /// MAJOR-3 (failed cleanup). The cleanup-failure diagnostic is written from inside a catch
    /// body. A throwing writer there would escape <c>FinalizeImproverOutcomeAsync</c> entirely
    /// and hide the primary result.
    /// <para>
    /// REMOVAL-PROOF: with the nonthrowing boundary the primary outcome still surfaces AND the
    /// cleanup failure is still composed onto it. Reintroducing the defect propagates the
    /// writer's IOException out of ExecuteAsync (or replaces the outcome), failing this test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Improver_ThrowingCleanupFailureLog_StillReturnsComposedResult()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var statusCalls = 0;
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                "diff --cached --name-only -z" => (0, StagedOutput("agents/coder.agents.md"), ""),
                // The CLEANUP's status (the 2nd) reports residue: the cleanup FAILS, so the
                // failure diagnostic is written from inside the catch body.
                "status --porcelain=v1 --untracked-files=all --ignored" =>
                    (0, ++statusCalls == 1 ? "" : "?? residue.txt\n", ""),
                _ => null,
            },
        };

        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var outWriter = new StringWriter();
        var throwingErr = new ThrowOnNeedleWriter("Config repo cleanup rejected");
        TaskResult result;
        try
        {
            Console.SetOut(outWriter);
            Console.SetError(throwingErr);

            var executor = new TaskExecutor(
                new MockAgentRunner(), gitOperations: git, configRepoDir: configRepoDir);
            result = await executor.ExecuteAsync(
                BuildImproverTask("improver-cleanupfaillog-throws"), TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }

        Assert.True(throwingErr.Fired, "the cleanup failure log did not throw — the test is vacuous");

        // THE PRIMARY RESULT SURVIVES and the cleanup failure is composed onto it: the
        // confirmed publication is retained, and the outcome is the truthful Failed/FAIL.
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.True(result.GitStatus!.Pushed);
        Assert.Contains("[Config Repo Cleanup Failure]", result.Output, StringComparison.Ordinal);
        Assert.Contains(
            result.Metrics.Issues,
            i => i.Contains("Config repo cleanup rejected", StringComparison.Ordinal));
    }

    // ── MAJOR-4: cancellation/retry paths keep the accumulated agent output ───

    /// <summary>
    /// MAJOR-4 (requested cancellation). Once the initial prompt has returned, a cancellation
    /// during size enforcement or publication must not replace the agent's real output with the
    /// bare <c>"Task was cancelled."</c> notice — finalization would then compose its cleanup
    /// diagnostics onto an impoverished result.
    /// <para>
    /// REMOVAL-PROOF: the accumulated initial/retry output must be preserved ahead of the
    /// notice, with Cancelled/CANCELLED semantics unchanged. Reintroducing the defect (a bare
    /// <c>Output = "Task was cancelled."</c>) drops the agent segments and fails this test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Improver_CancellationAfterPrompt_PreservesAccumulatedAgentOutput()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        using var cts = new CancellationTokenSource();
        const string agentSegment = "Improver reviewed every guidance file and appended a lesson";
        var git = new MockGitOperations
        {
            GitCommandResponder = args =>
            {
                // The publication's first command cancels the execution token — AFTER the
                // agent already produced its output.
                if (args == "add agents/*.agents.md")
                {
                    cts.Cancel();
                    throw new OperationCanceledException("shutdown", cts.Token);
                }

                return null;
            },
        };
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) => Task.FromResult(agentSegment),
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-cancel-preserves-output", configRepoDir, git, cts.Token, agentRunner);

        // NON-VACUITY: the agent really ran before the cancellation.
        Assert.Single(agentRunner.PromptCalls);

        // CANCELLED SEMANTICS UNCHANGED.
        Assert.Equal(TaskOutcome.Cancelled, result.Status);
        Assert.Equal("CANCELLED", result.Metrics!.Verdict);

        // THE ACCUMULATED AGENT OUTPUT SURVIVES, with the notice retained too.
        Assert.Contains(agentSegment, result.Output, StringComparison.Ordinal);
        Assert.Contains("Task was cancelled.", result.Output, StringComparison.Ordinal);
        Assert.StartsWith(agentSegment, result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// MAJOR-4 (retry exception). A later exception — here thrown by the size-enforcement
    /// RETRY prompt, after the initial prompt already returned — must not discard the
    /// accumulated initial output.
    /// <para>
    /// REMOVAL-PROOF: the generic handler must compose its sanitized diagnostic ONTO the
    /// accumulated evidence. Reintroducing the defect (a bare <c>Output = $"Error [{safe}]"</c>)
    /// loses the initial segment and fails this test. The diagnostic itself stays sanitized:
    /// the raw exception message never appears.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Improver_RetryException_PreservesAccumulatedAgentOutput()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var filePath = Path.Combine(configRepoDir, "agents", "coder.agents.md");
        const string initialSegment = "Initial improver analysis of every guidance file";
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = async (prompt, _, ct) =>
            {
                if (prompt.Contains("append-new/compress-old policy", StringComparison.Ordinal))
                {
                    // The size-enforcement RETRY throws — after the initial output exists.
                    throw new InvalidOperationException("condensation prompt exploded");
                }

                // The improver's own oversized edit, written from inside the agent callback.
                await File.WriteAllTextAsync(
                    filePath, new string('x', WorkerConstants.AgentsMdMaxCharacters + 1), ct);
                return initialSegment;
            },
        };
        var git = new MockGitOperations();

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-retry-throw-preserves-output", configRepoDir, git, agentRunner: agentRunner);

        // NON-VACUITY: both the initial prompt and the throwing retry prompt were delivered.
        Assert.Equal(2, agentRunner.PromptCalls.Count);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);

        // THE ACCUMULATED INITIAL OUTPUT SURVIVES the later retry exception.
        Assert.Contains(initialSegment, result.Output, StringComparison.Ordinal);
        Assert.StartsWith(initialSegment, result.Output, StringComparison.Ordinal);

        // SANITIZED: the classification only — the raw exception message never escapes.
        Assert.Contains("InvalidOperationException", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("exploded", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// MAJOR-4 + AC5 composition: a cancellation whose step-end cleanup ALSO fails keeps
    /// Cancelled/CANCELLED, preserves the accumulated agent output, AND reports the cleanup
    /// failure explicitly — all three at once.
    /// </summary>
    [Fact]
    public async Task Improver_CancellationWithCleanupFailure_KeepsOutputStatusAndDiagnostics()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        using var cts = new CancellationTokenSource();
        const string agentSegment = "Improver produced real analysis before the shutdown";
        var statusCalls = 0;
        var git = new MockGitOperations
        {
            GitCommandResponder = args =>
            {
                if (args == "add agents/*.agents.md")
                {
                    cts.Cancel();
                    throw new OperationCanceledException("shutdown", cts.Token);
                }

                // The CLEANUP's status (the 2nd) reports residue → cleanup failure.
                if (args == "status --porcelain=v1 --untracked-files=all --ignored")
                    return (0, ++statusCalls == 1 ? "" : "?? residue.txt\n", "");

                return null;
            },
        };
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) => Task.FromResult(agentSegment),
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-cancel-plus-cleanup-fail", configRepoDir, git, cts.Token, agentRunner);

        // Cancelled semantics preserved.
        Assert.Equal(TaskOutcome.Cancelled, result.Status);
        Assert.Equal("CANCELLED", result.Metrics!.Verdict);

        // Agent evidence preserved.
        Assert.Contains(agentSegment, result.Output, StringComparison.Ordinal);

        // Cleanup failure reported EXPLICITLY (AC5) rather than silently swallowed.
        Assert.Contains("[Config Repo Cleanup Failure]", result.Output, StringComparison.Ordinal);
        Assert.Contains(
            result.Metrics.Issues,
            i => i.Contains("Config repo cleanup rejected", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (d) + (e) REAL-GIT residue cells: residue created DURING the agent run
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// (d) REAL GIT — THE NO-CHANGE CELL. The agent makes NO guidance edit (so the staged diff
    /// is genuinely empty and the run is a normal no-change completion) but leaves every
    /// residue class behind DURING its run: a modified tracked file that it reverts to the
    /// baseline bytes, an untracked file and an ignored file.
    /// <para>
    /// The residue is created INSIDE the agent callback — i.e. AFTER the pre-run preparation's
    /// own <c>clean -fdx</c> — so only the STEP-END cleanup can remove it. That is exactly what
    /// this test pins: at ExecuteAsync RETURN the verbose porcelain status is EMPTY, the residue
    /// files are gone from disk, HEAD is still the baseline, and nothing was published.
    /// </para>
    /// <para>
    /// REMOVAL-PROOF: without the step-end cleanup the untracked/ignored files survive to the
    /// end state and the status assertion fails.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]   // tokenized seam route
    [InlineData(false)]  // legacy opaque route
    public async Task RealGit_Finalization_NoChangeRun_RemovesResidueCreatedDuringTheAgentRun(bool viaSeam)
    {
        using var playground = RealGitPlayground.Create(
            $"fin-nochange-{(viaSeam ? "seam" : "legacy")}",
            "REMOTE-BASELINE-V1\n",
            seedIgnoreRuleAndStagedBaseline: true);
        var worker = playground.WorkerDir;
        var baselineSha = playground.RemoteMainSha();
        var outsideSentinelBefore = File.ReadAllText(playground.OutsideSentinelPath);

        // The agent creates residue DURING the run and makes NO net guidance change: the
        // guidance file is written and then restored to its baseline bytes, so the staged diff
        // is empty and this is a genuine no-change completion.
        string? seenStatusDuringRun = null;
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) =>
            {
                File.WriteAllText(playground.GuidancePath, "TRANSIENT-EDIT\n");
                File.WriteAllText(Path.Combine(worker, "untracked-nochange.txt"), "untracked residue\n");
                File.WriteAllText(Path.Combine(worker, "ignored.txt"), "ignored residue\n");
                // Revert the tracked file so the publication stage sees an EMPTY staged diff.
                File.WriteAllText(playground.GuidancePath, "REMOTE-BASELINE-V1\n");
                // The residue is genuinely present at the end of the agent's turn.
                seenStatusDuringRun = playground.WorkerVerboseStatus();
                return Task.FromResult("No guidance changes were necessary");
            },
        };

        var (result, _, _) = await RunRealGitImproverAsync(
            $"realgit-fin-nochange-{viaSeam}", playground, viaSeam, agentRunner);

        // NON-VACUITY: the residue really existed when the agent finished.
        Assert.Single(agentRunner.PromptCalls);
        Assert.NotNull(seenStatusDuringRun);
        Assert.Contains("untracked-nochange.txt", seenStatusDuringRun!, StringComparison.Ordinal);
        Assert.Contains("ignored.txt", seenStatusDuringRun!, StringComparison.Ordinal);

        // A genuine NO-CHANGE completion: nothing staged, nothing published.
        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Equal("PASS", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(0, result.GitStatus.FilesChanged);
        Assert.DoesNotContain("[Config Repo Cleanup Failure]", result.Output, StringComparison.Ordinal);

        // ── THE END STATE AT ExecuteAsync RETURN ──────────────────────────────
        // Every residue class the agent created is gone, and the tree is verified clean.
        Assert.Equal("", playground.WorkerVerboseStatus());
        Assert.False(File.Exists(Path.Combine(worker, "untracked-nochange.txt")));
        Assert.False(File.Exists(Path.Combine(worker, "ignored.txt")));

        // The checkout still sits on the captured baseline with the baseline guidance content,
        // and the remote was never advanced.
        Assert.Equal(baselineSha, RealGitOutput(worker, "rev-parse", "HEAD^{commit}").Trim());
        Assert.Equal("REMOTE-BASELINE-V1\n", File.ReadAllText(playground.GuidancePath));
        Assert.Equal(baselineSha, playground.RemoteMainSha());

        // The OUTSIDE sentinel is untouched, byte for byte.
        Assert.Equal(outsideSentinelBefore, File.ReadAllText(playground.OutsideSentinelPath));
    }

    /// <summary>
    /// (e) REAL GIT — THE CONFIRMED-SUCCESS CELL. The agent publishes a real guidance edit AND
    /// leaves UNRELATED residue behind during its run: an unstaged edit to a tracked file that
    /// is not part of the publication, an untracked file, and an ignored file.
    /// <para>
    /// After the confirmed push, finalization must reset to the PUBLISHED tip (not the older
    /// baseline), so the published guidance survives in the working tree while every unrelated
    /// residue class is removed. This is the cell that distinguishes "clean up everything" from
    /// "clean up to the published state".
    /// </para>
    /// <para>
    /// REMOVAL-PROOF on two axes: dropping the cleanup leaves the unrelated residue behind, and
    /// resetting to the BASELINE instead of the published tip loses the published guidance from
    /// the working tree (and moves HEAD off the remote's tip).
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]   // tokenized seam route
    [InlineData(false)]  // legacy opaque route
    public async Task RealGit_Finalization_ConfirmedSuccess_KeepsPublishedTipAndCleansUnrelatedResidue(
        bool viaSeam)
    {
        using var playground = RealGitPlayground.Create(
            $"fin-success-residue-{(viaSeam ? "seam" : "legacy")}",
            "REMOTE-BASELINE-V1\n",
            seedIgnoreRuleAndStagedBaseline: true);
        var worker = playground.WorkerDir;
        var baselineSha = playground.RemoteMainSha();
        var outsideSentinelBefore = File.ReadAllText(playground.OutsideSentinelPath);

        const string publishedGuidance = "PUBLISHED-GUIDANCE-V2\n";
        string? seenStatusDuringRun = null;
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) =>
            {
                // The improver's REAL job: edit the guidance file (the publication stage
                // stages/commits/pushes only agents/*.agents.md).
                File.WriteAllText(playground.GuidancePath, publishedGuidance);

                // UNRELATED residue created during the same run — none of it is published.
                File.WriteAllText(Path.Combine(worker, "staged.txt"), "UNRELATED-TRACKED-EDIT\n");
                File.WriteAllText(Path.Combine(worker, "untracked-unrelated.txt"), "untracked residue\n");
                File.WriteAllText(Path.Combine(worker, "ignored.txt"), "ignored residue\n");
                seenStatusDuringRun = playground.WorkerVerboseStatus();
                return Task.FromResult("Improver published a guidance update");
            },
        };

        var (result, _, _) = await RunRealGitImproverAsync(
            $"realgit-fin-success-residue-{viaSeam}", playground, viaSeam, agentRunner);

        // NON-VACUITY: the unrelated residue really existed when the agent finished.
        Assert.Single(agentRunner.PromptCalls);
        Assert.NotNull(seenStatusDuringRun);
        Assert.Contains("untracked-unrelated.txt", seenStatusDuringRun!, StringComparison.Ordinal);
        Assert.Contains("staged.txt", seenStatusDuringRun!, StringComparison.Ordinal);
        Assert.Contains("ignored.txt", seenStatusDuringRun!, StringComparison.Ordinal);

        // CONFIRMED PUBLICATION.
        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Equal("PASS", result.Metrics!.Verdict);
        Assert.True(result.GitStatus!.Pushed);
        Assert.Equal([RealGitPlayground.GuidanceRelativePath], result.GitStatus.ChangedFiles);
        Assert.DoesNotContain("[Config Repo Cleanup Failure]", result.Output, StringComparison.Ordinal);

        // ── THE END STATE AT ExecuteAsync RETURN ──────────────────────────────
        // THE PUBLISHED GUIDANCE SURVIVES in the working tree...
        Assert.Equal(publishedGuidance, File.ReadAllText(playground.GuidancePath));
        // ...and on the remote, which really advanced past the baseline.
        var remoteShaAfter = playground.RemoteMainSha();
        Assert.NotEqual(baselineSha, remoteShaAfter);
        Assert.Equal(
            publishedGuidance,
            RealGitOutput(playground.RemoteDir, "show", "main:" + RealGitPlayground.GuidanceRelativePath));

        // THE LOCAL PUBLISHED TIP survives — finalization reset to the PUBLISHED SHA, never
        // back to the older baseline.
        var finalHead = RealGitOutput(worker, "rev-parse", "HEAD^{commit}").Trim();
        Assert.Equal(remoteShaAfter, finalHead);
        Assert.NotEqual(baselineSha, finalHead);

        // EVERY UNRELATED RESIDUE CLASS IS CLEANED, and the tree is verified clean.
        Assert.Equal("", playground.WorkerVerboseStatus());
        Assert.False(File.Exists(Path.Combine(worker, "untracked-unrelated.txt")));
        Assert.False(File.Exists(Path.Combine(worker, "ignored.txt")));
        // The unrelated TRACKED file is restored to its committed content (the edit is gone).
        Assert.Equal(
            RealGitPlayground.StagedBaselineContent,
            File.ReadAllText(Path.Combine(worker, "staged.txt")));

        // The OUTSIDE sentinel is untouched, byte for byte.
        Assert.Equal(outsideSentinelBefore, File.ReadAllText(playground.OutsideSentinelPath));
    }

    /// <summary>
    /// REAL GIT exhaustion on BOTH routes. After preparation, the agent creates a persistently
    /// oversized tracked guidance edit plus an independent staged delta, untracked residue, and
    /// ignored residue. One ExecuteAsync call must return Completed/SKIP only after finalization
    /// restores the exact baseline and strict-empty status; the bare remote must be unchanged.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealGit_ExhaustedCompression_RestoresBaselineAndLeavesRemoteUnchanged(bool viaSeam)
    {
        const string baselineContent = "REMOTE-BASELINE-V1\n";
        using var playground = RealGitPlayground.Create(
            $"exhausted-skip-{(viaSeam ? "seam" : "legacy")}",
            baselineContent,
            seedIgnoreRuleAndStagedBaseline: true);
        var worker = playground.WorkerDir;
        var baselineSha = playground.RemoteMainSha();
        var remoteGuidanceBefore = RealGitOutput(
            playground.RemoteDir, "show", "main:" + RealGitPlayground.GuidanceRelativePath);
        string? residueStatus = null;
        var call = 0;
        var runner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) =>
            {
                call++;
                if (call == 1)
                {
                    // TRACKED oversized guidance edit.
                    File.WriteAllText(playground.GuidancePath, new string('x', 8_001));
                    // STAGED index delta independent of the guidance edit.
                    File.WriteAllText(Path.Combine(worker, "staged.txt"), "STAGED-RESIDUE\n");
                    RealGit(worker, "add", "staged.txt");
                    // UNTRACKED and IGNORED residue.
                    File.WriteAllText(Path.Combine(worker, "untracked-exhausted.txt"), "untracked\n");
                    File.WriteAllText(Path.Combine(worker, "ignored.txt"), "ignored\n");
                    residueStatus = playground.WorkerVerboseStatus();
                }

                return Task.FromResult($"real-git-segment-{call}");
            },
        };

        var (result, _, legacyCommands) = await RunRealGitImproverAsync(
            $"realgit-exhausted-skip-{viaSeam}", playground, viaSeam, runner);

        Assert.Equal(4, runner.PromptCalls.Count);
        Assert.NotNull(residueStatus);
        var residue = ParsePorcelain(residueStatus!);
        Assert.Equal(" M", residue[RealGitPlayground.GuidanceRelativePath]);
        Assert.Equal("M ", residue["staged.txt"]);
        Assert.Equal("??", residue["untracked-exhausted.txt"]);
        Assert.Equal("!!", residue["ignored.txt"]);

        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Equal("SKIP", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Contains("guidance update SKIPPED", result.Output, StringComparison.Ordinal);

        // Exact local baseline and strict-empty status at the ExecuteAsync return boundary.
        Assert.Equal(baselineSha, playground.WorkerHeadSha());
        Assert.Equal(baselineContent, File.ReadAllText(playground.GuidancePath));
        Assert.Equal(RealGitPlayground.StagedBaselineContent,
            File.ReadAllText(Path.Combine(worker, "staged.txt")));
        Assert.Equal("", playground.WorkerVerboseStatus());
        Assert.False(File.Exists(Path.Combine(worker, "untracked-exhausted.txt")));
        Assert.False(File.Exists(Path.Combine(worker, "ignored.txt")));

        // Publication was never entered and the remote did not change.
        Assert.Equal(baselineSha, playground.RemoteMainSha());
        Assert.Equal(remoteGuidanceBefore, RealGitOutput(
            playground.RemoteDir, "show", "main:" + RealGitPlayground.GuidanceRelativePath));
        if (viaSeam)
            Assert.Empty(legacyCommands);
        else
        {
            Assert.DoesNotContain(legacyCommands, command => command.StartsWith("add ", StringComparison.Ordinal));
            Assert.DoesNotContain(legacyCommands, command => command.StartsWith("diff --cached", StringComparison.Ordinal));
            Assert.DoesNotContain(legacyCommands, command => command.StartsWith("commit ", StringComparison.Ordinal));
            Assert.DoesNotContain(legacyCommands, command => command.StartsWith("pull", StringComparison.Ordinal));
            Assert.DoesNotContain(legacyCommands, command => command.StartsWith("push", StringComparison.Ordinal));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ITERATION 3 — the evidence composition is scoped to the IMPROVER only
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a NON-IMPROVER task for the given role, with one repository and a feature branch
    /// so the ordinary (non-Improver) execution path runs.
    /// </summary>
    private static WorkTask BuildNonImproverTask(string id, WorkerRole role) => new()
    {
        TaskId = id,
        GoalId = $"goal-{id}",
        GoalDescription = "Test goal",
        Prompt = "Do the work",
        Role = role,
        Repositories = [Repo("repoA")],
        BranchInfo = new BranchSpec
        {
            Action = BranchAction.Checkout, BaseBranch = "main", FeatureBranch = "feature-branch",
        },
    };

    /// <summary>
    /// (b) NON-IMPROVER ROLES KEEP THEIR BASE CANCELLATION OUTPUT, BYTE FOR BYTE.
    /// <para>
    /// The Improver's evidence accumulator must NOT leak into other roles. A Coder, Tester or
    /// Reviewer that is cancelled AFTER its initial prompt returned real agent output must
    /// still return the BARE "Task was cancelled." notice — not the agent output plus the
    /// notice.
    /// </para>
    /// <para>
    /// REMOVAL-PROOF: the assertion pins the EXACT output string with Assert.Equal. Un-scoping
    /// the composition (letting the accumulator record/compose for every role) makes Output
    /// become "&lt;agent output&gt;\n\nTask was cancelled." and fails this test — a vague
    /// non-containment assertion would not.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    [InlineData(WorkerRole.Reviewer)]
    public async Task NonImprover_CancelledAfterPrompt_ReturnsBareBaseCancellationOutput(WorkerRole role)
    {
        using var cts = new CancellationTokenSource();
        const string agentSegment = "Non-improver agent produced substantial output before shutdown";

        // The git status call runs AFTER the prompt on every non-Improver role; cancelling
        // there reproduces "cancelled after the initial prompt returned".
        var git = new CancellingStatusGit(cts);
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) => Task.FromResult(agentSegment),
        };
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var result = await executor.ExecuteAsync(
            BuildNonImproverTask($"nonimprover-cancel-{role}", role), cts.Token);

        // NON-VACUITY: the agent really ran and produced output before the cancellation. The
        // Tester additionally fires its metrics-enforcement retry before the status probe, so
        // the expected prompt count is role-dependent.
        Assert.Equal(role == WorkerRole.Tester ? 2 : 1, agentRunner.PromptCalls.Count);
        Assert.True(cts.IsCancellationRequested, "the execution token was never cancelled — the test is vacuous");

        // BASE BEHAVIOR, EXACTLY: status, verdict and the BARE notice.
        Assert.Equal(TaskOutcome.Cancelled, result.Status);
        Assert.Equal("CANCELLED", result.Metrics!.Verdict);
        Assert.Equal("Task was cancelled.", result.Output);
        // The base cancellation result carries no GitStatus either.
        Assert.Null(result.GitStatus);
    }

    /// <summary>
    /// (b) NON-IMPROVER ROLES KEEP THEIR BASE FAILURE OUTPUT, BYTE FOR BYTE: the sanitized
    /// diagnostic ALONE, with no accumulated agent output prepended.
    /// <para>
    /// REMOVAL-PROOF: the exact output string is pinned. Un-scoping the composition prepends
    /// the agent's output and fails this test.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    [InlineData(WorkerRole.Reviewer)]
    public async Task NonImprover_FailureAfterPrompt_ReturnsSanitizedErrorAlone(WorkerRole role)
    {
        const string agentSegment = "Non-improver agent produced substantial output before failing";
        var git = new ThrowingStatusGit(new InvalidOperationException("status probe exploded"));
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) => Task.FromResult(agentSegment),
        };
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var result = await executor.ExecuteAsync(
            BuildNonImproverTask($"nonimprover-fail-{role}", role),
            TestContext.Current.CancellationToken);

        // NON-VACUITY: the agent really ran and produced output before the failure. (The
        // Tester additionally fires its metrics-enforcement retry before the status probe.)
        Assert.Equal(role == WorkerRole.Tester ? 2 : 1, agentRunner.PromptCalls.Count);

        // BASE BEHAVIOR, EXACTLY: the sanitized classification ALONE.
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Equal($"Error [{nameof(InvalidOperationException)}]", result.Output);
        // SANITIZED: neither the raw message nor the agent output leaks into the result.
        Assert.DoesNotContain("exploded", result.Output, StringComparison.Ordinal);
        Assert.Null(result.GitStatus);
    }

    /// <summary>
    /// (b) The NON-CANCELLATION OperationCanceledException boundary (an API timeout) keeps its
    /// base output for non-Improver roles too: the sanitized diagnostic alone, with the exact
    /// base wording.
    /// </summary>
    [Fact]
    public async Task NonImprover_ApiTimeoutAfterPrompt_ReturnsSanitizedTimeoutErrorAlone()
    {
        using var unrelatedCts = new CancellationTokenSource();
        var git = new ThrowingStatusGit(
            new OperationCanceledException("simulated timeout", unrelatedCts.Token));
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) => Task.FromResult("Coder output before the API timeout"),
        };
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var result = await executor.ExecuteAsync(
            BuildNonImproverTask("nonimprover-timeout", WorkerRole.Coder),
            TestContext.Current.CancellationToken);

        Assert.Single(agentRunner.PromptCalls);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        // BASE BEHAVIOR, EXACTLY — the diagnostic alone, no agent output prepended.
        Assert.Equal(
            $"Error: API call failed or timed out [{nameof(OperationCanceledException)}]",
            result.Output);
    }

    /// <summary>
    /// (b) THE NON-IMPROVER RETRY-PATH FORMATTING is unchanged: with an EMPTY initial agent
    /// output, the tester's metrics-enforcement retry still produces the base
    /// "\n\n[Test metrics enforcement]\n&lt;retry&gt;" shape — i.e. a LEADING blank separator —
    /// rather than the accumulator's "first segment wins" shape.
    /// <para>
    /// REMOVAL-PROOF: rewriting the helper to accumulate through the evidence object drops the
    /// leading separator (the empty initial output is skipped), so this exact assertion fails.
    /// </para>
    /// </summary>
    [Fact]
    public async Task NonImprover_TesterRetryWithEmptyInitialOutput_KeepsBaseFormatting()
    {
        const string retrySegment = "Tester retry produced the metrics";
        var promptCount = 0;
        var git = new MockGitOperations { FilesChanged = 0 };
        var agentRunner = new MockAgentRunner
        {
            // The INITIAL prompt returns EMPTY output; the metrics-enforcement retry returns
            // real text. The base helper concatenates onto the empty string, keeping the
            // leading "\n\n" separator.
            PromptResponder = (_, _, _) =>
                Task.FromResult(++promptCount == 1 ? string.Empty : retrySegment),
        };
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var result = await executor.ExecuteAsync(
            BuildNonImproverTask("nonimprover-tester-retry", WorkerRole.Tester),
            TestContext.Current.CancellationToken);

        // NON-VACUITY: the metrics-enforcement retry really fired.
        Assert.Equal(2, agentRunner.PromptCalls.Count);

        // BASE FORMATTING, EXACTLY: the empty initial output still contributes its separator.
        Assert.Equal($"\n\n[Test metrics enforcement]\n{retrySegment}", result.Output);
    }

    /// <summary>
    /// (a) THE IMPROVER SIDE STILL COMPOSES. The same "cancelled after the prompt" shape that
    /// returns the bare notice for a Coder/Tester/Reviewer must STILL preserve the accumulated
    /// agent output for the Improver — the scoping fixed the leak without weakening the
    /// Improver contract.
    /// <para>
    /// REMOVAL-PROOF: dropping the Improver-scoped composition (disabling the accumulator for
    /// every role) makes Output the bare notice and fails this test, which is the exact
    /// converse of the non-Improver tests above.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Improver_CancelledAfterPrompt_StillComposesAccumulatedOutput()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        using var cts = new CancellationTokenSource();
        const string agentSegment = "Improver analysis retained across the cancellation";
        var git = new MockGitOperations
        {
            GitCommandResponder = args =>
            {
                if (args == "add agents/*.agents.md")
                {
                    cts.Cancel();
                    throw new OperationCanceledException("shutdown", cts.Token);
                }

                return null;
            },
        };
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = (_, _, _) => Task.FromResult(agentSegment),
        };

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-scoped-cancel", configRepoDir, git, cts.Token, agentRunner);

        Assert.Single(agentRunner.PromptCalls);
        Assert.True(cts.IsCancellationRequested);

        // Cancelled semantics preserved, AND the accumulated evidence is composed with the
        // notice — the exact opposite of the non-Improver contract.
        Assert.Equal(TaskOutcome.Cancelled, result.Status);
        Assert.Equal("CANCELLED", result.Metrics!.Verdict);
        Assert.Equal($"{agentSegment}\n\nTask was cancelled.", result.Output);
    }

    /// <summary>
    /// (a) The Improver's RETRY-exception path likewise still composes: an exception thrown by
    /// the size-enforcement retry keeps the accumulated initial output ahead of the sanitized
    /// diagnostic.
    /// </summary>
    [Fact]
    public async Task Improver_RetryExceptionAfterPrompt_StillComposesAccumulatedOutput()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var filePath = Path.Combine(configRepoDir, "agents", "coder.agents.md");
        const string initialSegment = "Improver initial analysis retained across the retry throw";
        var agentRunner = new MockAgentRunner
        {
            PromptResponder = async (prompt, _, ct) =>
            {
                if (prompt.Contains("append-new/compress-old policy", StringComparison.Ordinal))
                    throw new InvalidOperationException("condensation prompt exploded");

                await File.WriteAllTextAsync(
                    filePath, new string('x', WorkerConstants.AgentsMdMaxCharacters + 1), ct);
                return initialSegment;
            },
        };
        var git = new MockGitOperations();

        var (result, _, _) = await RunImproverLegacyAsync(
            "improver-scoped-retry-throw", configRepoDir, git, agentRunner: agentRunner);

        Assert.Equal(2, agentRunner.PromptCalls.Count);
        Assert.Equal(TaskOutcome.Failed, result.Status);

        // The accumulated output is composed with the sanitized diagnostic, exactly.
        Assert.Equal(
            $"{initialSegment}\n\nError [{nameof(InvalidOperationException)}]",
            result.Output);
        Assert.DoesNotContain("exploded", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// An <see cref="IGitOperations"/> whose status probe REQUESTS the execution cancellation
    /// and throws — reproducing "cancelled after the initial prompt returned" on the ordinary
    /// non-Improver path. Everything else behaves like a no-op clone/branch transport.
    /// </summary>
    private sealed class CancellingStatusGit(CancellationTokenSource cts) : IGitOperations
    {
        public Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct) => Task.CompletedTask;
        public Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;
        public Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct) => Task.CompletedTask;
        public Task PushBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> HasUncommittedChangesAsync(string repoDir, CancellationToken ct) => Task.FromResult(false);
        public Task<string?> GetMergeBaseAsync(string repoDir, string baseBranch, CancellationToken ct)
            => Task.FromResult<string?>(null);
        public Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(
            string workDir, string args, CancellationToken ct)
            => Task.FromResult((0, string.Empty, string.Empty));
        public Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5) => Task.CompletedTask;

        public Task<GitChangeSummary> GetGitStatusAsync(string repoDir, string? baseBranch, CancellationToken ct)
        {
            cts.Cancel();
            throw new OperationCanceledException("shutdown", cts.Token);
        }
    }

    /// <summary>
    /// An <see cref="IGitOperations"/> whose status probe THROWS the supplied exception — the
    /// failure counterpart of <see cref="CancellingStatusGit"/>.
    /// </summary>
    private sealed class ThrowingStatusGit(Exception failure) : IGitOperations
    {
        public Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct) => Task.CompletedTask;
        public Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;
        public Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct) => Task.CompletedTask;
        public Task PushBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> HasUncommittedChangesAsync(string repoDir, CancellationToken ct) => Task.FromResult(false);
        public Task<string?> GetMergeBaseAsync(string repoDir, string baseBranch, CancellationToken ct)
            => Task.FromResult<string?>(null);
        public Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(
            string workDir, string args, CancellationToken ct)
            => Task.FromResult((0, string.Empty, string.Empty));
        public Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5) => Task.CompletedTask;

        public Task<GitChangeSummary> GetGitStatusAsync(string repoDir, string? baseBranch, CancellationToken ct)
            => throw failure;
    }
}
