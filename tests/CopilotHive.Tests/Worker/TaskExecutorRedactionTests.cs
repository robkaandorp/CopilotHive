using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Flow B of the credential-URL redaction goal: the worker's task repositories.
/// <para>
/// <see cref="TaskExecutor"/> logs <c>repo.Url</c> before cloning, renders several git exception
/// messages into logs, and STORES push failures into the task result that travels back to the
/// orchestrator. A fake <see cref="IGitOperations"/> feeds credential-bearing text into each of
/// those boundaries and asserts (a) the RAW url still reaches the git layer and (b) no captured
/// log line or task-result field carries the credential. No process, no network, no timing.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class TaskExecutorRedactionTests : IDisposable
{
    private const string Token = "ghp_task_executor_secret";
    private const string CredentialUrl =
        $"https://x-access-token:{Token}@github.com/acme/widgets.git";
    private const string RedactedUrl = "https://github.com/acme/widgets.git";

    private readonly StringWriter _stdOut = new();
    private readonly StringWriter _stdErr = new();
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;
    private readonly List<string> _tempDirs = [];

    public TaskExecutorRedactionTests()
    {
        _originalOut = Console.Out;
        _originalErr = Console.Error;
        Console.SetOut(_stdOut);
        Console.SetError(_stdErr);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
        _stdOut.Dispose();
        _stdErr.Dispose();

        foreach (var dir in _tempDirs.Where(Directory.Exists))
            TestHelpers.ForceDeleteDirectory(dir);
    }

    /// <summary>All console output (stdout and stderr) captured so far.</summary>
    private string AllOutput => _stdOut.ToString() + _stdErr.ToString();

    // ── Fakes ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A fully scriptable <see cref="IGitOperations"/> that records what it was handed.
    /// </summary>
    private sealed class FakeGit : IGitOperations
    {
        /// <summary>Every URL passed to <see cref="CloneRepositoryAsync"/>, in order.</summary>
        public List<string> ClonedUrls { get; } = [];

        /// <summary>Exception thrown by <see cref="CheckoutBranchAsync"/>, or null.</summary>
        public Exception? CheckoutException { get; set; }

        /// <summary>Exception thrown by <see cref="PushBranchAsync"/>, or null.</summary>
        public Exception? PushException { get; set; }

        /// <summary>Number of files reported as changed by <see cref="GetGitStatusAsync"/>.</summary>
        public int FilesChanged { get; set; } = 3;

        /// <summary>Scripted results for <see cref="RunGitCommandAsync"/>, keyed on the args.</summary>
        public Func<string, (int ExitCode, string Stdout, string Stderr)?>? GitCommandResponder { get; set; }

        /// <summary>Scripted throws for <see cref="RunGitCommandAsync"/>, keyed on the args.</summary>
        public Func<string, Exception?>? GitCommandThrower { get; set; }

        /// <summary>Every argument string passed to <see cref="RunGitCommandAsync"/>, in order.</summary>
        public List<string> GitCommands { get; } = [];

        public Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct)
        {
            ClonedUrls.Add(url);
            return Task.CompletedTask;
        }

        public Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct)
            => CheckoutException is { } ex ? throw ex : Task.CompletedTask;

        public Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct)
            => Task.CompletedTask;

        public Task PushBranchAsync(string repoDir, string branch, CancellationToken ct)
            => PushException is { } ex ? throw ex : Task.CompletedTask;

        public Task<GitChangeSummary> GetGitStatusAsync(string repoDir, string? baseBranch, CancellationToken ct)
            => Task.FromResult(new GitChangeSummary
            {
                FilesChanged = FilesChanged,
                Insertions = 4,
                Deletions = 1,
            });

        public Task<bool> HasUncommittedChangesAsync(string repoDir, CancellationToken ct)
            => Task.FromResult(false);

        public Task<string?> GetMergeBaseAsync(string repoDir, string baseBranch, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(
            string workDir, string args, CancellationToken ct)
        {
            GitCommands.Add(args);
            if (GitCommandThrower?.Invoke(args) is { } ex)
                throw ex;
            return Task.FromResult(GitCommandResponder?.Invoke(args) ?? (0, string.Empty, string.Empty));
        }

        public Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5)
            => Task.CompletedTask;
    }

    /// <summary>A minimal <see cref="IAgentRunner"/> that returns a fixed response.</summary>
    private sealed class StubAgentRunner : IAgentRunner
    {
        public TestResultReport? LastTestReport => null;
        public WorkerReport? LastWorkerReport => null;

        private object? _session;

        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(WorkerRole role, string agentsMdContent) { }
        public void SetMaxContextTokens(int maxTokens) { }
        public void SetCompactionModel(string? model) { }
        public void SetCompactionMaxTokens(int? maxTokens) { }
        public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) { }
        public void SetSession(object? session) => _session = session;
        public object? GetSession() => _session;
        public int GetContextUsagePercent() => 0;
        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) { }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetSessionAsync(string? model, ReasoningEffort? effort, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
            => Task.FromResult("agent output");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>An <see cref="ISessionClient"/> whose failures carry a credential-bearing URL.</summary>
    private sealed class FailingSessionClient(Exception failure) : ISessionClient
    {
        public Task<string?> GetSessionAsync(string sessionId, CancellationToken ct) => throw failure;

        public Task SaveSessionAsync(string sessionId, string sessionJson, CancellationToken ct)
            => throw failure;
    }

    // ── Boundary: the pre-clone repo.Url log ──────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_PreCloneLog_IsCredentialFree()
    {
        var git = new FakeGit();
        var executor = new TaskExecutor(new StubAgentRunner(), gitOperations: git);

        await executor.ExecuteAsync(
            BuildTask(CredentialUrl), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(Token, AllOutput);
        Assert.DoesNotContain("x-access-token", AllOutput);
        Assert.Contains($"Cloning widgets from {RedactedUrl}", AllOutput);
    }

    /// <summary>
    /// Redaction is a LOG concern only — the git layer still receives the raw credential URL, so
    /// cloning keeps working.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ClonesWithTheRawCredentialUrl()
    {
        var git = new FakeGit();
        var executor = new TaskExecutor(new StubAgentRunner(), gitOperations: git);

        await executor.ExecuteAsync(
            BuildTask(CredentialUrl), TestContext.Current.CancellationToken);

        Assert.Equal(CredentialUrl, Assert.Single(git.ClonedUrls));
    }

    [Fact]
    public async Task ExecuteAsync_CredentialFreeUrl_IsLoggedUnchanged()
    {
        var git = new FakeGit();
        var executor = new TaskExecutor(new StubAgentRunner(), gitOperations: git);

        await executor.ExecuteAsync(
            BuildTask(RedactedUrl), TestContext.Current.CancellationToken);

        Assert.Contains($"Cloning widgets from {RedactedUrl}", AllOutput);
    }

    // ── Boundary: the checkout-failure log ────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CheckoutFailureLog_IsCredentialFree()
    {
        var git = new FakeGit
        {
            CheckoutException = new GitOperationException(
                $"Failed to checkout branch 'feature': fatal: unable to access '{CredentialUrl}/': 403"),
        };
        var executor = new TaskExecutor(new StubAgentRunner(), gitOperations: git);
        var task = BuildTask(CredentialUrl);
        task = task with { BranchInfo = new BranchSpec
        {
            Action = BranchAction.Checkout, BaseBranch = "main", FeatureBranch = "feature",
        } };

        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        Assert.Contains("Checkout failed", AllOutput);
        Assert.DoesNotContain(Token, AllOutput);
        Assert.Contains($"unable to access '{RedactedUrl}/'", AllOutput);
    }

    // ── Boundary: the iteration-SHA-capture failure log ───────────────────────

    [Fact]
    public async Task ExecuteAsync_IterationShaCaptureFailureLog_IsCredentialFree()
    {
        var git = new FakeGit
        {
            GitCommandThrower = args => args.Contains("rev-parse")
                ? new InvalidOperationException(
                    $"git rev-parse failed for remote {CredentialUrl}")
                : null,
        };
        var executor = new TaskExecutor(new StubAgentRunner(), gitOperations: git);

        await executor.ExecuteAsync(
            BuildTask(CredentialUrl), TestContext.Current.CancellationToken);

        Assert.Contains("Could not capture iteration start SHA", AllOutput);
        Assert.DoesNotContain(Token, AllOutput);
        Assert.Contains(RedactedUrl, AllOutput);
    }

    // ── Boundary: PushBranchAsync — logged AND stored in the task result ──────

    [Fact]
    public async Task ExecuteAsync_PushFailureLog_IsCredentialFree()
    {
        var git = new FakeGit
        {
            PushException = new GitOperationException(
                $"Failed to push branch 'feature': fatal: unable to access '{CredentialUrl}/': 403"),
        };
        var executor = new TaskExecutor(new StubAgentRunner(), gitOperations: git);

        await executor.ExecuteAsync(
            BuildTask(CredentialUrl), TestContext.Current.CancellationToken);

        Assert.Contains("Push failed for widgets", AllOutput);
        Assert.DoesNotContain(Token, AllOutput);
    }

    /// <summary>
    /// The SAME message is stored into the task result, which travels to the orchestrator to be
    /// logged and persisted. Both the output body and the metrics issues must be credential-free.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_PushFailureTaskResult_IsCredentialFree()
    {
        var git = new FakeGit
        {
            PushException = new GitOperationException(
                $"Failed to push branch 'feature': fatal: unable to access '{CredentialUrl}/': 403"),
        };
        var executor = new TaskExecutor(new StubAgentRunner(), gitOperations: git);

        var result = await executor.ExecuteAsync(
            BuildTask(CredentialUrl), TestContext.Current.CancellationToken);

        Assert.Contains("[Git Push Errors]", result.Output);
        Assert.DoesNotContain(Token, result.Output);
        Assert.DoesNotContain("x-access-token", result.Output);

        var issue = Assert.Single(result.Metrics!.Issues, i => i.Contains("Push failed"));
        Assert.DoesNotContain(Token, issue);
        Assert.Contains($"unable to access '{RedactedUrl}/'", issue);
    }

    /// <summary>
    /// A credential-free push failure keeps its exact original wording — existing behavior and
    /// existing assertions are untouched.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_PushFailureWithoutCredential_KeepsExactMessage()
    {
        var git = new FakeGit
        {
            PushException = new GitOperationException(
                "Failed to push branch 'feature': Permission denied"),
        };
        var executor = new TaskExecutor(new StubAgentRunner(), gitOperations: git);

        var result = await executor.ExecuteAsync(
            BuildTask(RedactedUrl), TestContext.Current.CancellationToken);

        Assert.Contains(
            "Push failed for widgets: Failed to push branch 'feature': Permission denied",
            result.Metrics!.Issues);
    }

    // ── Boundary: the clone-failure task result (SafeExceptionLog) ────────────

    /// <summary>
    /// A clone failure is caught by the generic handler, which renders the exception through
    /// <see cref="SafeExceptionLog.Describe"/> — type names only, never the message. The result
    /// therefore carries no credential even though the exception message did.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenCloneThrows_TaskResultCarriesNoCredential()
    {
        var git = new ThrowingCloneGit(new GitOperationException(
            $"Failed to clone '{CredentialUrl}': fatal: Authentication failed"));
        var executor = new TaskExecutor(new StubAgentRunner(), gitOperations: git);

        var result = await executor.ExecuteAsync(
            BuildTask(CredentialUrl), TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.DoesNotContain(Token, result.Output);
        Assert.DoesNotContain(Token, string.Join('\n', result.Metrics!.Issues));
        Assert.DoesNotContain(Token, AllOutput);
        Assert.Contains(nameof(GitOperationException), result.Output);
    }

    /// <summary>A git fake whose clone always throws.</summary>
    private sealed class ThrowingCloneGit(Exception failure) : IGitOperations
    {
        public Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct)
            => throw failure;
        public Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct)
            => Task.CompletedTask;
        public Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct)
            => Task.CompletedTask;
        public Task PushBranchAsync(string repoDir, string branch, CancellationToken ct)
            => Task.CompletedTask;
        public Task<GitChangeSummary> GetGitStatusAsync(string repoDir, string? baseBranch, CancellationToken ct)
            => Task.FromResult(new GitChangeSummary());
        public Task<bool> HasUncommittedChangesAsync(string repoDir, CancellationToken ct)
            => Task.FromResult(false);
        public Task<string?> GetMergeBaseAsync(string repoDir, string baseBranch, CancellationToken ct)
            => Task.FromResult<string?>(null);
        public Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(
            string workDir, string args, CancellationToken ct)
            => Task.FromResult((0, string.Empty, string.Empty));
        public Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5) => Task.CompletedTask;
    }

    // ── Boundary: the session load/save failure logs ──────────────────────────

    [Fact]
    public async Task ExecuteAsync_SessionLoadFailureLog_IsCredentialFree()
    {
        var git = new FakeGit();
        var sessions = new FailingSessionClient(new InvalidOperationException(
            $"session store unreachable at {CredentialUrl}"));
        var executor = new TaskExecutor(
            new StubAgentRunner(), gitOperations: git, sessionClient: sessions);

        var task = BuildTask(CredentialUrl) with { SessionId = "session-1" };
        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        Assert.Contains("Failed to load session 'session-1'", AllOutput);
        Assert.DoesNotContain(Token, AllOutput);
        Assert.Contains($"session store unreachable at {RedactedUrl}", AllOutput);
    }

    [Fact]
    public async Task ExecuteAsync_SessionSaveFailureLog_IsCredentialFree()
    {
        var git = new FakeGit();
        var sessions = new SaveOnlyFailingSessionClient(new InvalidOperationException(
            $"session store unreachable at {CredentialUrl}"));
        var executor = new TaskExecutor(
            new StubAgentRunner(), gitOperations: git, sessionClient: sessions);

        var task = BuildTask(CredentialUrl) with { SessionId = "session-2" };
        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        Assert.Contains("Failed to save session 'session-2'", AllOutput);
        Assert.DoesNotContain(Token, AllOutput);
        Assert.Contains($"session store unreachable at {RedactedUrl}", AllOutput);
    }

    /// <summary>A session client that loads a real session but fails on save.</summary>
    private sealed class SaveOnlyFailingSessionClient(Exception failure) : ISessionClient
    {
        public Task<string?> GetSessionAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<string?>("{}");

        public Task SaveSessionAsync(string sessionId, string sessionJson, CancellationToken ct)
            => throw failure;
    }

    // ── Boundary: the improver's config-repo git logs ─────────────────────────

    [Fact]
    public async Task ExecuteAsync_Improver_ConfigRepoPullFailureLog_IsCredentialFree()
    {
        var configRepo = CreateConfigRepoDir();
        var git = new FakeGit
        {
            GitCommandResponder = args => args.StartsWith("pull --ff-only")
                ? (1, string.Empty, $"fatal: unable to access '{CredentialUrl}/': 403")
                : null,
        };
        var executor = new TaskExecutor(
            new StubAgentRunner(), gitOperations: git, configRepoDir: configRepo);

        await executor.ExecuteAsync(
            BuildImproverTask(), TestContext.Current.CancellationToken);

        Assert.Contains("Config repo pull failed", AllOutput);
        Assert.DoesNotContain(Token, AllOutput);
        Assert.Contains($"unable to access '{RedactedUrl}/'", AllOutput);
    }

    [Fact]
    public async Task ExecuteAsync_Improver_ConfigRepoPushFailureLog_IsCredentialFree()
    {
        var configRepo = CreateConfigRepoDir();
        var git = new FakeGit
        {
            GitCommandResponder = args => args switch
            {
                "add agents/*.agents.md" => (0, string.Empty, string.Empty),
                "diff --cached --name-only -z" => (0, "agents/coder.agents.md\0", string.Empty),
                "push" => (1, string.Empty, $"fatal: unable to access '{CredentialUrl}/': 403"),
                _ => null,
            },
        };
        var executor = new TaskExecutor(
            new StubAgentRunner(), gitOperations: git, configRepoDir: configRepo);

        await executor.ExecuteAsync(
            BuildImproverTask(), TestContext.Current.CancellationToken);

        Assert.Contains("git push failed", AllOutput);
        Assert.DoesNotContain(Token, AllOutput);
        Assert.Contains($"unable to access '{RedactedUrl}/'", AllOutput);
    }

    /// <summary>
    /// A push that throws (rather than returning a non-zero exit code) hits the catch-all
    /// config-repo push log, which is redacted too.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Improver_ConfigRepoPushThrowLog_IsCredentialFree()
    {
        var configRepo = CreateConfigRepoDir();
        var git = new FakeGit
        {
            GitCommandResponder = args => args switch
            {
                "add agents/*.agents.md" => (0, string.Empty, string.Empty),
                "diff --cached --name-only -z" => (0, "agents/coder.agents.md\0", string.Empty),
                _ => null,
            },
            GitCommandThrower = args => args == "push"
                ? new InvalidOperationException($"push to {CredentialUrl} exploded")
                : null,
        };
        var executor = new TaskExecutor(
            new StubAgentRunner(), gitOperations: git, configRepoDir: configRepo);

        await executor.ExecuteAsync(
            BuildImproverTask(), TestContext.Current.CancellationToken);

        Assert.Contains("Push failed:", AllOutput);
        Assert.DoesNotContain(Token, AllOutput);
        Assert.Contains($"push to {RedactedUrl} exploded", AllOutput);
    }

    /// <summary>
    /// The improver's successful-pull log renders git STDOUT, which also echoes the remote.
    /// The stdout VALUE itself is never mutated — only its log rendering is redacted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Improver_ConfigRepoPullSuccessLog_IsCredentialFree()
    {
        var configRepo = CreateConfigRepoDir();
        var git = new FakeGit
        {
            GitCommandResponder = args => args.StartsWith("pull --ff-only")
                ? (0, $"Already up to date with {CredentialUrl}", string.Empty)
                : null,
        };
        var executor = new TaskExecutor(
            new StubAgentRunner(), gitOperations: git, configRepoDir: configRepo);

        await executor.ExecuteAsync(
            BuildImproverTask(), TestContext.Current.CancellationToken);

        Assert.Contains("Config repo up to date", AllOutput);
        Assert.DoesNotContain(Token, AllOutput);
        Assert.Contains($"Already up to date with {RedactedUrl}", AllOutput);
    }

    // ── Functional data is never mutated ──────────────────────────────────────

    /// <summary>
    /// The captured iteration SHA comes from RAW stdout and must be stored verbatim — redaction
    /// must never touch functional data.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_IterationStartSha_IsTakenFromRawStdout()
    {
        const string Sha = "0123456789abcdef0123456789abcdef01234567";
        var git = new FakeGit
        {
            GitCommandResponder = args => args == "rev-parse HEAD"
                ? (0, Sha + "\n", string.Empty)
                : null,
        };
        var executor = new TaskExecutor(new StubAgentRunner(), gitOperations: git);

        var result = await executor.ExecuteAsync(
            BuildTask(CredentialUrl), TestContext.Current.CancellationToken);

        Assert.Equal(Sha, result.IterationStartSha);
    }

    /// <summary>
    /// The agent's own output is passed through untouched, even when it happens to contain a
    /// URL-looking string — only the enumerated boundaries redact.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AgentOutput_IsNotRedacted()
    {
        var git = new FakeGit();
        var executor = new TaskExecutor(new StubAgentRunner(), gitOperations: git);

        var result = await executor.ExecuteAsync(
            BuildTask(CredentialUrl), TestContext.Current.CancellationToken);

        Assert.Equal("agent output", result.Output);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WorkTask BuildTask(string url) => new()
    {
        TaskId = "task-redaction",
        GoalId = "goal-redaction",
        GoalDescription = "Redaction goal",
        Prompt = "do the thing",
        Role = WorkerRole.Coder,
        Repositories = [new TargetRepository { Name = "widgets", Url = url, DefaultBranch = "main" }],
        BranchInfo = new BranchSpec
        {
            Action = BranchAction.Create, BaseBranch = "main", FeatureBranch = "feature",
        },
    };

    private static WorkTask BuildImproverTask() => new()
    {
        TaskId = "task-improver",
        GoalId = "goal-improver",
        GoalDescription = "Improve agents",
        Prompt = "improve",
        Role = WorkerRole.Improver,
        Repositories = [],
    };

    private string CreateConfigRepoDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"CfgRepoRedact_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        _tempDirs.Add(dir);
        return dir;
    }
}
