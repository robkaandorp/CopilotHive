using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for the branch observation, the failure metadata capture and the structured resume
/// log line of the v0.37.0 restart-at-failure flow
/// (<see cref="GoalDispatcher.ResumeGoalAsync"/>, variant A).
/// </summary>
public sealed class ResumeRestartObservationTests
{
    private static readonly TimeSpan TestResumeTimeout = TimeSpan.FromMilliseconds(50);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static HiveConfigFile ConfigWithRepos(params string[] repoNames)
    {
        var config = new HiveConfigFile
        {
            Workers =
            {
                ["coder"] = new WorkerConfig { Model = "coder-model" },
            },
        };
        foreach (var name in repoNames)
        {
            config.Repositories.Add(new RepositoryConfig
            {
                Name = name,
                Url = $"https://github.com/test/{name}.git",
                DefaultBranch = "main",
            });
        }

        return config;
    }

    private static Goal FailedGoal(string id, string reason, params string[] repoNames) => new()
    {
        Id = id,
        Description = "Restart observation goal",
        Status = GoalStatus.Failed,
        FailureReason = reason,
        RepositoryNames = [.. repoNames],
    };

    private static GoalPipeline FailedPipelineWithBranch(GoalPipelineManager manager, Goal goal)
    {
        var pipeline = manager.CreatePipeline(goal, maxRetries: 3, maxIterations: 3);
        pipeline.CoderBranch = $"copilothive/{goal.Id}";
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Coding, 1, 1));
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Testing, 1, 2));
        pipeline.AdvanceTo(GoalPhase.Failed);
        return pipeline;
    }

    private static GoalDispatcher CreateDispatcher(
        IGoalStore goalStore,
        GoalPipelineManager pipelineManager,
        ILogger<GoalDispatcher> logger,
        HiveConfigFile config,
        IDistributedBrain? brain = null)
    {
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);
        return new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain ?? new RestartFakeBrain(),
            config: config,
            goalStore: goalStore);
    }

    /// <summary>
    /// Runs a variant-A resume with the supplied lister seam and returns the capturing logger
    /// plus the planning contexts the Brain observed.
    /// </summary>
    private static async Task<(CapturingLogger Logger, RestartFakeBrain Brain, bool Resumed)> RunResumeAsync(
        string goalId,
        string failureReason,
        string[] repoNames,
        Func<string, CancellationToken, Task<List<string>>> lister,
        CancellationToken ct)
    {
        var goalStore = new InMemoryGoalStore();
        var goal = FailedGoal(goalId, failureReason, repoNames);
        goalStore.AddGoal(goal);

        var manager = new GoalPipelineManager();
        FailedPipelineWithBranch(manager, goal);

        var logger = new CapturingLogger();
        var brain = new RestartFakeBrain();
        var dispatcher = CreateDispatcher(goalStore, manager, logger, ConfigWithRepos(repoNames), brain);
        dispatcher.ResumeTimeout = TestResumeTimeout;
        dispatcher.BranchListerForTest = lister;

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, ct);
        return (logger, brain, resumed);
    }

    private static string ResumeLogLine(CapturingLogger logger) =>
        logger.Entries.Single(e => e.Message.StartsWith("ResumeRestart for goal", StringComparison.Ordinal)).Message;

    // ── Aggregation vectors ──────────────────────────────────────────────────

    [Fact]
    public async Task Observation_AllReposReportBranch_AggregatesToPresent()
    {
        const string goalId = "obs-present";
        var (logger, brain, resumed) = await RunResumeAsync(
            goalId,
            "Review rejected the changes",
            ["repo-a", "repo-b"],
            (repo, ct) => Task.FromResult(new List<string> { "main", $"copilothive/{goalId}" }),
            TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Contains("branch-state=present", ResumeLogLine(logger), StringComparison.Ordinal);
        Assert.Contains("repos=[repo-a:true, repo-b:true]", ResumeLogLine(logger), StringComparison.Ordinal);

        var context = Assert.Single(brain.PlanContexts);
        Assert.NotNull(context);
        Assert.Contains(
            $"The feature branch `copilothive/{goalId}` carries the prior iterations' merged work. "
            + "Checkout the existing branch; do NOT recreate it from scratch.",
            context,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Observation_OneRepoMissingBranch_AggregatesToAbsent()
    {
        const string goalId = "obs-absent";
        var (logger, brain, resumed) = await RunResumeAsync(
            goalId,
            "Review rejected the changes",
            ["repo-a", "repo-b"],
            (repo, ct) => Task.FromResult(repo == "repo-a"
                ? new List<string> { $"copilothive/{goalId}" }
                : new List<string> { "main" }),
            TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Contains("branch-state=absent", ResumeLogLine(logger), StringComparison.Ordinal);
        Assert.Contains("repos=[repo-a:true, repo-b:false]", ResumeLogLine(logger), StringComparison.Ordinal);
        Assert.Contains(
            "The feature branch appears absent — checkout may fall back to re-creating it from the base; "
            + "prior branch work may be lost.",
            Assert.Single(brain.PlanContexts)!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Observation_MixedPresentAndUnknown_AggregatesToUnknown()
    {
        const string goalId = "obs-mixed";
        var (logger, brain, resumed) = await RunResumeAsync(
            goalId,
            "Review rejected the changes",
            ["repo-a", "repo-b"],
            (repo, ct) => repo == "repo-a"
                ? Task.FromResult(new List<string> { $"copilothive/{goalId}" })
                : Task.FromException<List<string>>(new InvalidOperationException("repo not cloned")),
            TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Contains("branch-state=unknown", ResumeLogLine(logger), StringComparison.Ordinal);
        Assert.Contains("repos=[repo-a:true, repo-b:unknown]", ResumeLogLine(logger), StringComparison.Ordinal);
        Assert.Contains(
            "The branch state could not be verified; checkout may fall back to re-creating it from the base.",
            Assert.Single(brain.PlanContexts)!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Observation_ZeroRepositories_AggregatesToUnknown()
    {
        const string goalId = "obs-zero-repos";
        var invoked = false;
        var (logger, brain, resumed) = await RunResumeAsync(
            goalId,
            "Review rejected the changes",
            [],
            (repo, ct) =>
            {
                invoked = true;
                return Task.FromResult(new List<string>());
            },
            TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.False(invoked, "no repositories means the lister must never run");
        Assert.Contains("branch-state=unknown", ResumeLogLine(logger), StringComparison.Ordinal);
        Assert.Contains("repos=[]", ResumeLogLine(logger), StringComparison.Ordinal);
        Assert.Contains(
            "The branch state could not be verified; checkout may fall back to re-creating it from the base.",
            Assert.Single(brain.PlanContexts)!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Observation_ThrowingRepository_YieldsUnknown_AndLoopContinues()
    {
        const string goalId = "obs-throw";
        var visited = new List<string>();
        var (logger, _, resumed) = await RunResumeAsync(
            goalId,
            "Review rejected the changes",
            ["repo-a", "repo-b"],
            (repo, ct) =>
            {
                visited.Add(repo);
                if (repo == "repo-a")
                    throw new InvalidOperationException("listing blew up synchronously");
                return Task.FromResult(new List<string> { $"copilothive/{goalId}" });
            },
            TestContext.Current.CancellationToken);

        Assert.True(resumed);
        // The loop continued past the throwing repository.
        Assert.Equal(["repo-a", "repo-b"], visited);
        Assert.Contains("repos=[repo-a:unknown, repo-b:true]", ResumeLogLine(logger), StringComparison.Ordinal);
        Assert.Contains("branch-state=unknown", ResumeLogLine(logger), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Observation_BranchMatching_IsCaseInsensitive()
    {
        const string goalId = "obs-case";
        var (logger, _, resumed) = await RunResumeAsync(
            goalId,
            "Review rejected the changes",
            ["repo-a"],
            (repo, ct) => Task.FromResult(new List<string> { $"COPILOTHIVE/{goalId.ToUpperInvariant()}" }),
            TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Contains("branch-state=present", ResumeLogLine(logger), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Observation_RepositorySource_IsThePipelineGoalsRepositoryNames()
    {
        const string goalId = "obs-repo-source";
        var observed = new List<string>();
        var (_, _, resumed) = await RunResumeAsync(
            goalId,
            "Review rejected the changes",
            ["alpha-repo", "beta-repo", "gamma-repo"],
            (repo, ct) =>
            {
                observed.Add(repo);
                return Task.FromResult(new List<string>());
            },
            TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(["alpha-repo", "beta-repo", "gamma-repo"], observed);
    }

    [Fact]
    public async Task Observation_DisposesThePerRepositoryCancellationTokenSource()
    {
        const string goalId = "obs-cts-dispose";
        var tokens = new List<CancellationToken>();
        var (_, _, resumed) = await RunResumeAsync(
            goalId,
            "Review rejected the changes",
            ["repo-a", "repo-b"],
            (repo, ct) =>
            {
                tokens.Add(ct);
                return Task.FromResult(new List<string> { $"copilothive/{goalId}" });
            },
            TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(2, tokens.Count);
        // Distinct sources: each repo gets a FRESH CancellationTokenSource.
        Assert.NotEqual(tokens[0], tokens[1]);
        foreach (var token in tokens)
        {
            Assert.True(token.CanBeCanceled, "the lister must never receive CancellationToken.None");
            // Touching the wait handle of a token whose source was disposed throws.
            Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
        }
    }

    // ── The two deadline bounds ──────────────────────────────────────────────

    [Fact]
    public async Task Observation_CancellationHonoringLister_AbortsAtTheDeadline_AndLoopContinues()
    {
        const string goalId = "obs-deadline-honoring";
        var visited = new List<string>();
        CancellationToken firstToken = default;
        Task<List<string>>? firstListing = null;

        var (logger, _, resumed) = await RunResumeAsync(
            goalId,
            "Review rejected the changes",
            ["stalled-repo", "healthy-repo"],
            (repo, ct) =>
            {
                visited.Add(repo);
                if (repo != "stalled-repo")
                    return Task.FromResult(new List<string> { $"copilothive/{goalId}" });

                // A well-behaved listing observes its token and aborts when it fires.
                var tcs = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => tcs.TrySetCanceled(ct));
                firstToken = ct;
                firstListing = tcs.Task;
                return tcs.Task;
            },
            TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(["stalled-repo", "healthy-repo"], visited);
        Assert.Contains("repos=[stalled-repo:unknown, healthy-repo:true]", ResumeLogLine(logger), StringComparison.Ordinal);

        // The independent per-repo token fired: the parked listing was aborted, not orphaned.
        // The bounded wait keeps the assertion decisive: a lister handed a non-cancellable token
        // never completes, and the resulting TimeoutException fails this test instead of hanging.
        Assert.NotNull(firstListing);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => firstListing!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(firstToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Observation_CancellationIgnoringLister_HitsTheWaitAsyncBound_AndTheOutliverIsObserved()
    {
        const string goalId = "obs-deadline-ignoring";
        // No RunContinuationsAsynchronously: the fault continuation runs inline on SetException,
        // so the assertion below needs no polling.
        var stalled = new TaskCompletionSource<List<string>>();
        var visited = new List<string>();

        var (logger, _, resumed) = await RunResumeAsync(
            goalId,
            "Review rejected the changes",
            ["ignoring-repo", "healthy-repo"],
            (repo, ct) =>
            {
                visited.Add(repo);
                // Deliberately ignores the token — only the defensive WaitAsync bound can save us.
                return repo == "ignoring-repo"
                    ? stalled.Task
                    : Task.FromResult(new List<string> { $"copilothive/{goalId}" });
            },
            TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(["ignoring-repo", "healthy-repo"], visited);
        Assert.Contains("repos=[ignoring-repo:unknown, healthy-repo:true]", ResumeLogLine(logger), StringComparison.Ordinal);

        // The outliving task is observed: its eventual fault is logged, never unobserved.
        stalled.SetException(new InvalidOperationException("late listing failure"));
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("faulted after the resume observation deadline", StringComparison.Ordinal)
            && e.Message.Contains("ignoring-repo", StringComparison.Ordinal));
    }

    // ── The structured log line ──────────────────────────────────────────────

    [Fact]
    public async Task ResumeLogLine_RendersAllFields()
    {
        const string goalId = "obs-log-line";
        var (logger, _, resumed) = await RunResumeAsync(
            goalId,
            "Testing failed:\r\n  3 tests   red",
            ["repo-a", "repo-b"],
            (repo, ct) => Task.FromResult(repo == "repo-a"
                ? new List<string> { $"copilothive/{goalId}" }
                : new List<string> { "main" }),
            TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(
            $"ResumeRestart for goal {goalId}: failed-phase=Testing, failure-reason=Testing failed: 3 tests red, "
            + $"branch=copilothive/{goalId}, branch-state=absent, repos=[repo-a:true, repo-b:false]",
            ResumeLogLine(logger));
    }

    // ── SanitizedSingleLine ──────────────────────────────────────────────────

    [Fact]
    public void SanitizedSingleLine_ReplacesCarriageReturnsAndLineFeedsWithSpaces()
    {
        Assert.Equal("a b c", GoalDispatcher.SanitizedSingleLine("a\rb\nc"));
    }

    [Fact]
    public void SanitizedSingleLine_RemovesOtherControlCharacters()
    {
        Assert.Equal("abc", GoalDispatcher.SanitizedSingleLine("a\u0001b\u0007c\u007f"));
        // A tab is a control character too — removed, not turned into a space.
        Assert.Equal("ab", GoalDispatcher.SanitizedSingleLine("a\tb"));
    }

    [Fact]
    public void SanitizedSingleLine_CollapsesConsecutiveSpacesAndTrims()
    {
        Assert.Equal("a b", GoalDispatcher.SanitizedSingleLine("   a     b   "));
        Assert.Equal("a b", GoalDispatcher.SanitizedSingleLine("\r\n a \r\n\r\n b \r\n"));
    }

    [Fact]
    public void SanitizedSingleLine_ExactlyThreeHundredCharacters_IsNotTruncated()
    {
        var input = new string('x', 300);
        var result = GoalDispatcher.SanitizedSingleLine(input);
        Assert.Equal(300, result.Length);
        Assert.Equal(input, result);
    }

    [Fact]
    public void SanitizedSingleLine_ThreeHundredAndOneCharacters_IsTruncatedTo297PlusEllipsis()
    {
        var input = new string('x', 301);
        var result = GoalDispatcher.SanitizedSingleLine(input);
        Assert.Equal(300, result.Length);
        Assert.Equal(new string('x', 297) + "...", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void SanitizedSingleLine_NullOrWhitespace_RendersUnknown(string? input)
    {
        Assert.Equal("unknown", GoalDispatcher.SanitizedSingleLine(input));
    }

    // ── FailedPhase resolution ───────────────────────────────────────────────

    [Fact]
    public void ResolveFailedPhase_ReturnsTheLastWorkerFacingEntry()
    {
        var pipeline = new GoalPipeline(new Goal { Id = "phase-goal", Description = "d" });
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Coding, 1, 1));
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Testing, 1, 2));
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Review, 1, 3));
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Failed, 1, 4));

        Assert.Equal("Review", GoalDispatcher.ResolveFailedPhase(pipeline));
    }

    [Fact]
    public void ResolveFailedPhase_TerminalOnlyLog_ReturnsUnknown()
    {
        var pipeline = new GoalPipeline(new Goal { Id = "phase-terminal", Description = "d" });
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Planning, 1, 1));
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Failed, 1, 2));

        Assert.Equal("unknown", GoalDispatcher.ResolveFailedPhase(pipeline));
    }

    [Fact]
    public void ResolveFailedPhase_EmptyLog_ReturnsUnknown()
    {
        var pipeline = new GoalPipeline(new Goal { Id = "phase-empty", Description = "d" });
        Assert.Equal("unknown", GoalDispatcher.ResolveFailedPhase(pipeline));
    }

    // ── The seams ────────────────────────────────────────────────────────────

    [Fact]
    public void Seams_HaveTheDocumentedDefaults()
    {
        var goalStore = new InMemoryGoalStore();
        var dispatcher = CreateDispatcher(
            goalStore, new GoalPipelineManager(), new CapturingLogger(), ConfigWithRepos("repo-a"));

        Assert.Equal(TimeSpan.FromSeconds(10), dispatcher.ResumeTimeout);
        Assert.Null(dispatcher.BranchListerForTest);
    }

    // ── Private fakes ────────────────────────────────────────────────────────

    /// <summary>Collecting logger that captures every entry for assertion.</summary>
    private sealed class CapturingLogger : ILogger<GoalDispatcher>
    {
        private readonly Lock _gate = new();

        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
                Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>Brain stub that records the planning context of every planning call.</summary>
    private sealed class RestartFakeBrain : IDistributedBrain
    {
        public List<string?> PlanContexts { get; } = [];

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
        {
            PlanContexts.Add(additionalContext);
            return Task.FromResult(PlanResult.Success(IterationPlan.Default()));
        }

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult("summary");

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("proceed"));

        public Task UpdateModelAsync(string model, int? maxContextTokens, ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            Task.CompletedTask;

        public BrainStats? GetStats() => null;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public bool GoalSessionExists(string goalId) => false;

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}

/// <summary>
/// Tests for the two-variant resume gate, the case-sensitive branch-name invariant, the
/// plan-shape check and the non-destructive dispatch of
/// <see cref="GoalDispatcher.ResumeGoalAsync"/>.
/// </summary>
public sealed class ResumeRestartGateTests
{
    private static readonly TimeSpan TestResumeTimeout = TimeSpan.FromMilliseconds(50);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static HiveConfigFile ConfigWithRepo() => new()
    {
        Repositories =
        [
            new RepositoryConfig { Name = "test-repo", Url = "https://github.com/test/repo.git", DefaultBranch = "main" },
        ],
        Workers =
        {
            ["coder"] = new WorkerConfig { Model = "coder-model" },
        },
    };

    private static Goal FailedGoal(string id, string reason) => new()
    {
        Id = id,
        Description = "Restart gate goal",
        Status = GoalStatus.Failed,
        FailureReason = reason,
        RepositoryNames = ["test-repo"],
    };

    private static GoalPipeline FailedPipeline(GoalPipelineManager manager, Goal goal, string? coderBranch)
    {
        var pipeline = manager.CreatePipeline(goal, maxRetries: 3, maxIterations: 3);
        // Exhaust the budget so the branchless variant's exhaustion precondition is realistic.
        while (pipeline.IterationBudget.TryConsume()) { }
        pipeline.CoderBranch = coderBranch;
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Coding, 1, 1));
        pipeline.AdvanceTo(GoalPhase.Failed);
        manager.PersistFull(pipeline);
        return pipeline;
    }

    private static GoalDispatcher CreateDispatcher(
        IGoalStore goalStore,
        GoalPipelineManager pipelineManager,
        ILogger<GoalDispatcher>? logger = null,
        IDistributedBrain? brain = null,
        IWorkerGateway? workerGateway = null)
    {
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            workerGateway ?? new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger ?? NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain ?? new GateFakeBrain(),
            config: ConfigWithRepo(),
            goalStore: goalStore);
        dispatcher.ResumeTimeout = TestResumeTimeout;
        return dispatcher;
    }

    // ── Variant A entry ──────────────────────────────────────────────────────

    [Fact]
    public async Task VariantA_NonCancellationFailureWithCanonicalBranch_Resumes()
    {
        const string goalId = "gate-variant-a";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, "Review requested changes and the iteration bailed");
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        var pipeline = FailedPipeline(manager, goal, $"copilothive/{goalId}");

        var brain = new GateFakeBrain();
        var dispatcher = CreateDispatcher(store, manager, brain: brain);
        dispatcher.BranchListerForTest = (repo, ct) => Task.FromResult(new List<string> { $"copilothive/{goalId}" });

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(GoalStatus.InProgress, goal.Status);
        Assert.Null(goal.FailureReason);
        Assert.NotEqual(GoalPhase.Failed, pipeline.Phase);

        // The failure-informed context reached planning.
        var context = Assert.Single(brain.PlanContexts);
        Assert.NotNull(context);
        Assert.Contains("Review requested changes and the iteration bailed", context, StringComparison.Ordinal);
        Assert.Contains("Failed phase: Coding", context, StringComparison.Ordinal);
    }

    // ── The cancellation predicate ───────────────────────────────────────────

    [Theory]
    [InlineData("Cancelled by user")]
    [InlineData("cancelled by user")]
    [InlineData("CANCELLED BY USER")]
    public async Task CancellationFailure_IsNeverResumable(string failureReason)
    {
        const string goalId = "gate-cancelled";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, failureReason);
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        var pipeline = FailedPipeline(manager, goal, $"copilothive/{goalId}");

        var dispatcher = CreateDispatcher(store, manager);

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.False(resumed);
        Assert.Equal(GoalStatus.Failed, goal.Status);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
    }

    /// <summary>
    /// The predicate is EQUALITY, not <c>Contains</c>: a reason that merely mentions the
    /// cancellation wording is a different failure and stays resumable.
    /// </summary>
    [Theory]
    [InlineData("Cancelled by user (test)")]
    [InlineData("Cancelled by")]
    [InlineData("Worker reported: cancelled by user timeout")]
    [InlineData("Exceeded max iterations")]
    public async Task NonEqualCancellationLikeReasons_StillResume(string failureReason)
    {
        const string goalId = "gate-not-cancelled";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, failureReason);
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        FailedPipeline(manager, goal, $"copilothive/{goalId}");

        var dispatcher = CreateDispatcher(store, manager);
        dispatcher.BranchListerForTest = (repo, ct) => Task.FromResult(new List<string>());

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(GoalStatus.InProgress, goal.Status);
    }

    // ── Variant B identity ───────────────────────────────────────────────────

    [Fact]
    public async Task VariantB_BranchlessExhaustionResume_PassesNullContext_AndNeverObserves()
    {
        const string goalId = "gate-variant-b";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, "Exceeded max iterations");
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        FailedPipeline(manager, goal, coderBranch: null);

        var logger = new CapturingLogger();
        var brain = new GateFakeBrain();
        var dispatcher = CreateDispatcher(store, manager, logger: logger, brain: brain);
        var listerInvoked = false;
        dispatcher.BranchListerForTest = (repo, ct) =>
        {
            listerInvoked = true;
            return Task.FromResult(new List<string>());
        };

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(GoalStatus.InProgress, goal.Status);
        // The context stays NULL — the historical behaviour, byte for byte.
        Assert.Null(Assert.Single(brain.PlanContexts));
        Assert.False(listerInvoked, "variant B must never observe branches");
        Assert.DoesNotContain(logger.Entries, e =>
            e.Message.StartsWith("ResumeRestart for goal", StringComparison.Ordinal));
    }

    /// <summary>
    /// Variant B keeps the historical behaviour: NO plan-shape check. A DocWriting-first plan is
    /// accepted by the state machine, so this proves the Coding-first rule is variant-A only
    /// (a Testing-first plan is rejected by the pre-existing state machine, not by this rule).
    /// </summary>
    [Fact]
    public async Task VariantB_NonCodingFirstPlan_IsAccepted()
    {
        const string goalId = "gate-variant-b-shape";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, "Exceeded max iterations");
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        var pipeline = FailedPipeline(manager, goal, coderBranch: null);

        var brain = new GateFakeBrain
        {
            Plan = new IterationPlan { Phases = [GoalPhase.DocWriting, GoalPhase.Merging], Reason = "docs first" },
        };
        var dispatcher = CreateDispatcher(store, manager, brain: brain);

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(GoalPhase.DocWriting, pipeline.Phase);
        Assert.DoesNotContain(store.StatusUpdates, u => u.Reason == "Resume plan must start with Coding");
    }

    [Fact]
    public async Task BranchlessNonExhaustionFailure_ReturnsFalse()
    {
        const string goalId = "gate-branchless-other";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, "Review rejected the changes");
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        var pipeline = FailedPipeline(manager, goal, coderBranch: null);

        var dispatcher = CreateDispatcher(store, manager);

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.False(resumed);
        Assert.Equal(GoalStatus.Failed, goal.Status);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
    }

    // ── The branch-name invariant ────────────────────────────────────────────

    [Fact]
    public async Task BranchMismatch_ReturnsFalse_LogsWarning_AndMutatesNothing()
    {
        const string goalId = "gate-branch-mismatch";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, "Review rejected the changes");
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        var pipeline = FailedPipeline(manager, goal, "copilothive/some-other-goal");

        var logger = new CapturingLogger();
        var dispatcher = CreateDispatcher(store, manager, logger: logger);
        dispatcher.BranchListerForTest = (repo, ct) =>
            throw new InvalidOperationException("the observation must never run for a rejected branch");

        var before = Snapshot(goal, pipeline);

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.False(resumed);
        Assert.Equal(before, Snapshot(goal, pipeline));
        Assert.Empty(store.StatusUpdates);
        Assert.Empty(store.GoalUpdates);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("does not match the canonical branch", StringComparison.Ordinal));
    }

    /// <summary>
    /// Git branch names are case-sensitive, so the invariant is ORDINAL: a case-only difference
    /// is a mismatch, never a match.
    /// </summary>
    [Fact]
    public async Task BranchCaseOnlyMismatch_IsRejected()
    {
        const string goalId = "gate-branch-case";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, "Review rejected the changes");
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        var pipeline = FailedPipeline(manager, goal, $"COPILOTHIVE/{goalId}");

        var logger = new CapturingLogger();
        var dispatcher = CreateDispatcher(store, manager, logger: logger);

        var before = Snapshot(goal, pipeline);

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.False(resumed);
        Assert.Equal(before, Snapshot(goal, pipeline));
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("does not match the canonical branch", StringComparison.Ordinal));
    }

    // ── Dispatch removal proofs ──────────────────────────────────────────────

    [Fact]
    public async Task VariantA_DispatchesWithCheckoutBranchAction()
    {
        const string goalId = "gate-dispatch-checkout";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, "Review rejected the changes");
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        FailedPipeline(manager, goal, $"copilothive/{goalId}");

        var gateway = new CapturingWorkerGateway();
        var dispatcher = CreateDispatcher(store, manager, workerGateway: gateway);
        dispatcher.BranchListerForTest = (repo, ct) => Task.FromResult(new List<string> { $"copilothive/{goalId}" });

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        var task = Assert.Single(gateway.SentTasks);
        Assert.NotNull(task.BranchInfo);
        Assert.Equal(BranchAction.Checkout, task.BranchInfo!.Action);
        Assert.Equal($"copilothive/{goalId}", task.BranchInfo.FeatureBranch);
    }

    [Fact]
    public async Task VariantB_DispatchesWithCreateBranchAction()
    {
        const string goalId = "gate-dispatch-create";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, "Exceeded max iterations");
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        FailedPipeline(manager, goal, coderBranch: null);

        var gateway = new CapturingWorkerGateway();
        var dispatcher = CreateDispatcher(store, manager, workerGateway: gateway);

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        var task = Assert.Single(gateway.SentTasks);
        Assert.NotNull(task.BranchInfo);
        Assert.Equal(BranchAction.Create, task.BranchInfo!.Action);
    }

    // ── The plan-shape check ─────────────────────────────────────────────────

    [Fact]
    public async Task VariantA_PlanNotStartingWithCoding_FailsTheGoal_AndReturnsTrue()
    {
        const string goalId = "gate-plan-shape";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, "Review rejected the changes");
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        var pipeline = FailedPipeline(manager, goal, $"copilothive/{goalId}");

        var gateway = new CapturingWorkerGateway();
        var brain = new GateFakeBrain
        {
            // DocWriting-first is accepted by the pipeline state machine, so ONLY the variant-A
            // shape check can reject it — removing the check would resume and dispatch instead.
            Plan = new IterationPlan { Phases = [GoalPhase.DocWriting, GoalPhase.Merging], Reason = "docs first" },
        };
        var dispatcher = CreateDispatcher(store, manager, brain: brain, workerGateway: gateway);
        dispatcher.BranchListerForTest = (repo, ct) => Task.FromResult(new List<string> { $"copilothive/{goalId}" });

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        var failure = Assert.Single(store.StatusUpdates, u => u.Status == GoalStatus.Failed);
        Assert.Equal("Resume plan must start with Coding", failure.Reason);
        Assert.Empty(gateway.SentTasks);
    }

    // ── The installation-time plan rejection ─────────────────────────────────

    /// <summary>
    /// THE INSTALLATION-TIME REJECTION (variant B, branchless): the branchless resume has NO
    /// Coding-first check, so the plan reaches INSTALLATION. A DocWriting-first plan is accepted
    /// by that first-phase check but REJECTED by the state machine because it does not end with
    /// Merging — the resume must fail the goal cleanly instead of letting the ArgumentException
    /// escape and strand the goal with no pipeline dispatch.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: unwrap the <c>SetPlan</c>/<c>StartIteration</c> pair and
    /// <see cref="GoalDispatcher.ResumeGoalAsync"/> throws out of the resume: the goal keeps its
    /// InProgress status, no Failed status update is recorded and this vector fails on the
    /// propagated exception instead of the assertions below.
    /// </remarks>
    [Fact]
    public async Task VariantB_PlanRejectedByStartIteration_FailsGoalWithResumePlanRejectedReason()
    {
        const string goalId = "gate-install-reject-b";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, "Exceeded max iterations");
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        var pipeline = FailedPipeline(manager, goal, coderBranch: null);

        var gateway = new CapturingWorkerGateway();
        var brain = new GateFakeBrain
        {
            // DocWriting-first is ACCEPTED by the state machine's first-phase rule, so ONLY the
            // missing-Merging rule of StartIteration can reject it.
            Plan = new IterationPlan { Phases = [GoalPhase.DocWriting, GoalPhase.Review], Reason = "no merging" },
        };
        var dispatcher = CreateDispatcher(store, manager, brain: brain, workerGateway: gateway);

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(GoalStatus.Failed, goal.Status);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Equal(GoalPhase.Failed, pipeline.StateMachine.Phase);
        var failure = Assert.Single(store.StatusUpdates, u => u.Status == GoalStatus.Failed);
        Assert.StartsWith("Resume plan rejected:", failure.Reason);
        Assert.Contains("must end with Merging", failure.Reason, StringComparison.Ordinal);
        Assert.Equal(failure.Reason, goal.FailureReason);
        // The rejection preceded any dispatch: no worker ever saw a task.
        Assert.Empty(gateway.SentTasks);
    }

    /// <summary>
    /// THE INSTALLATION-TIME REJECTION (variant A): a Coding-first plan PASSES the variant-A
    /// shape check and is rejected only at installation because it does not end with Merging —
    /// the exact gap between the two checks.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: without the installation guard the ArgumentException escapes
    /// <see cref="GoalDispatcher.ResumeGoalAsync"/> — no Failed status update is recorded and this
    /// vector fails on the propagated exception.
    /// </remarks>
    [Fact]
    public async Task VariantA_CodingFirstPlanNotEndingWithMerging_FailsGoalWithResumePlanRejectedReason()
    {
        const string goalId = "gate-install-reject-a";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, "Review rejected the changes");
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        var pipeline = FailedPipeline(manager, goal, $"copilothive/{goalId}");

        var gateway = new CapturingWorkerGateway();
        var brain = new GateFakeBrain
        {
            // Coding-first, so the variant-A shape check passes; it is the Merging rule that the
            // state machine enforces at installation.
            Plan = new IterationPlan { Phases = [GoalPhase.Coding, GoalPhase.Testing], Reason = "truncated plan" },
        };
        var dispatcher = CreateDispatcher(store, manager, brain: brain, workerGateway: gateway);
        dispatcher.BranchListerForTest = (repo, ct) => Task.FromResult(new List<string> { $"copilothive/{goalId}" });

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(GoalStatus.Failed, goal.Status);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        var failure = Assert.Single(store.StatusUpdates, u => u.Status == GoalStatus.Failed);
        Assert.StartsWith("Resume plan rejected:", failure.Reason);
        Assert.Contains("must end with Merging", failure.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(store.StatusUpdates, u => u.Reason == "Resume plan must start with Coding");
        Assert.Empty(gateway.SentTasks);
    }

    // ── The retry-budget reset on resume ─────────────────────────────────────

    /// <summary>
    /// A successful resume restores BOTH task-level retry budgets to their full allowance — the
    /// fresh test/review retries that accompany the fresh iteration top-up — and the persisted row
    /// reflects the reset (<c>test_retries = 0</c>, <c>review_retries = 0</c>) with
    /// <c>MaxRetries</c> unchanged.
    /// </summary>
    /// <remarks>
    /// THE ANTI-VACUOUS PRECONDITION: both budgets are genuinely EXHAUSTED before the resume (this
    /// is the production shape of an "Exceeded max test retries" failure), so the post-resume
    /// <c>Used == 0</c>/<c>Remaining == 3</c> assertions cannot pass over budgets that were never
    /// consumed.
    /// <para>
    /// REMOVAL-PROOF: delete the <c>pipeline.ResetRetryBudgets()</c> call from
    /// <see cref="GoalDispatcher.ResumeGoalAsync"/> and this vector fails — the in-memory budgets
    /// still report <c>Used == 3</c>/<c>IsExhausted == true</c> and the persisted row still carries
    /// <c>test_retries = 3</c>/<c>review_retries = 3</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Resume_ExhaustedRetryBudgets_AreRestoredInMemoryAndPersisted()
    {
        const string goalId = "gate-retry-reset";
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        await using var store = new PipelineStore(dbContext, NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store);

        var goalStore = new RecordingGoalStore();
        var goal = FailedGoal(goalId, "Exceeded max test retries");
        goalStore.AddGoal(goal);

        var pipeline = ExhaustedRetryPipeline(manager, goal, $"copilothive/{goalId}");

        Assert.True(pipeline.TestRetryBudget.IsExhausted);
        Assert.True(pipeline.ReviewRetryBudget.IsExhausted);
        var maxRetriesBefore = pipeline.MaxRetries;
        Assert.Equal(3, maxRetriesBefore);

        var dispatcher = CreateDispatcher(goalStore, manager);
        dispatcher.BranchListerForTest = (repo, ct) => Task.FromResult(new List<string> { $"copilothive/{goalId}" });

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(0, pipeline.TestRetryBudget.Used);
        Assert.Equal(pipeline.MaxRetries, pipeline.TestRetryBudget.Remaining);
        Assert.False(pipeline.TestRetryBudget.IsExhausted);
        Assert.Equal(0, pipeline.ReviewRetryBudget.Used);
        Assert.Equal(pipeline.MaxRetries, pipeline.ReviewRetryBudget.Remaining);
        Assert.False(pipeline.ReviewRetryBudget.IsExhausted);
        // A reset, not a top-up: MaxRetries derives from ReviewRetryBudget.Allowed and is unchanged.
        Assert.Equal(maxRetriesBefore, pipeline.MaxRetries);
        Assert.Equal(maxRetriesBefore, pipeline.ReviewRetryBudget.Allowed);
        Assert.Equal(maxRetriesBefore, pipeline.TestRetryBudget.Allowed);

        // The PERSISTED ROW carries the reset counters — read back through an INDEPENDENT channel
        // (a no-tracking SQL query, not the identity-mapped instance the writes targeted), so this
        // is the durable half of the fix rather than an echo of the in-memory state.
        var row = dbContext.Pipelines.AsNoTracking().Single(p => p.GoalId == goalId);
        Assert.Equal(0, row.TestRetries);
        Assert.Equal(0, row.ReviewRetries);
        Assert.Equal(maxRetriesBefore, row.MaxRetries);
    }

    /// <summary>
    /// A REFUSED resume (branch mismatch) mutates NOTHING — the exhaustion state of both task-level
    /// retry budgets, in memory and in the persisted row, survives the refusal.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF for the ORDERING half of the contract: move the
    /// <c>pipeline.ResetRetryBudgets()</c> call above the refusal checks and this vector fails —
    /// the refused resume would leave both budgets restored instead of untouched.
    /// </remarks>
    [Fact]
    public async Task RefusedResume_BranchMismatch_LeavesRetryBudgetsUntouched()
    {
        const string goalId = "gate-refused-retry-reset";
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        await using var store = new PipelineStore(dbContext, NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store);

        var goalStore = new RecordingGoalStore();
        var goal = FailedGoal(goalId, "Review rejected the changes");
        goalStore.AddGoal(goal);

        var pipeline = ExhaustedRetryPipeline(manager, goal, "copilothive/some-other-goal");

        Assert.True(pipeline.TestRetryBudget.IsExhausted);
        Assert.True(pipeline.ReviewRetryBudget.IsExhausted);

        var dispatcher = CreateDispatcher(goalStore, manager);
        dispatcher.BranchListerForTest = (repo, ct) =>
            throw new InvalidOperationException("the observation must never run for a rejected branch");

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.False(resumed);
        Assert.Equal(GoalStatus.Failed, goal.Status);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Equal(3, pipeline.TestRetryBudget.Used);
        Assert.Equal(0, pipeline.TestRetryBudget.Remaining);
        Assert.True(pipeline.TestRetryBudget.IsExhausted);
        Assert.Equal(3, pipeline.ReviewRetryBudget.Used);
        Assert.Equal(0, pipeline.ReviewRetryBudget.Remaining);
        Assert.True(pipeline.ReviewRetryBudget.IsExhausted);

        // The durable exhaustion state survives the refusal, read back through the same
        // no-tracking channel as the success vector.
        var row = dbContext.Pipelines.AsNoTracking().Single(p => p.GoalId == goalId);
        Assert.Equal(3, row.TestRetries);
        Assert.Equal(3, row.ReviewRetries);
    }

    // ── Parity: the shared rule answers what ResumeGoalAsync answers ─────────

    /// <summary>
    /// PARITY between the shared rule and the backend, over the fixture shapes above: for each row
    /// the answer <see cref="GoalResumeEligibility.IsResumable"/> gives for the goal and its
    /// pipeline (observed with the SAME read-only lookup the dashboard uses) is EXACTLY the answer
    /// <see cref="GoalDispatcher.ResumeGoalAsync"/> gives. The dispatcher's answer is taken from the
    /// SAME fixture instance, so a divergence in either direction fails the row.
    /// </summary>
    /// <remarks>
    /// Each row is a real resume attempt through the production entry point, so the row's expected
    /// value is not merely the rule restated: the assertion pins what the backend ACTUALLY did
    /// against what the rule PREDICTED. The cancelled, branch-mismatch and case-only-mismatch rows
    /// are the isolation vectors for rules 1 and 3; the exhaustion and "Worker failed" rows for
    /// rule 4.
    /// </remarks>
    [Theory]
    // (reason, coderBranch, expected)
    [InlineData("Review rejected the changes", "canonical", true)]
    [InlineData("Worker failed: build error in Coding", "canonical", true)]
    [InlineData("Exceeded max iterations", "canonical", true)]
    [InlineData("Cancelled by user", "canonical", false)]
    [InlineData("cancelled by user", "canonical", false)]
    [InlineData("Cancelled by user (test)", "canonical", true)]
    [InlineData("Review rejected the changes", "other", false)]
    [InlineData("Review rejected the changes", "case-only", false)]
    [InlineData("Review rejected the changes", null, false)]
    [InlineData("Worker failed: build error in Coding", null, false)]
    [InlineData("Exceeded max iterations", null, true)]
    public async Task IsResumable_MatchesResumeGoalAsync_ForEveryFixtureShape(
        string reason, string? branchKind, bool expected)
    {
        var goalId = "parity-fixture-shape";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, reason);
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        var branch = branchKind switch
        {
            "canonical" => $"copilothive/{goalId}",
            "other" => "copilothive/some-other-goal",
            "case-only" => $"COPILOTHIVE/{goalId}",
            null => null,
            _ => throw new InvalidOperationException($"Unhandled branch kind '{branchKind}'"),
        };
        FailedPipeline(manager, goal, branch);

        // The rule reads the pipeline through the SAME side-effect-free lookup the dashboard uses.
        var observed = manager.GetPhaseAndCoderBranch(goalId);
        Assert.NotNull(observed);
        var predicted = GoalResumeEligibility.IsResumable(goal, observed!.Value.Phase, observed.Value.CoderBranch);

        var dispatcher = CreateDispatcher(store, manager);
        dispatcher.BranchListerForTest = (repo, ct) =>
            Task.FromResult(branch is null ? [] : new List<string> { branch });
        var actual = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.Equal(expected, predicted);
        Assert.Equal(predicted, actual);
    }

    /// <summary>
    /// The MUTATING backend must not change the rule's answer once it has run: after a SUCCESSFUL
    /// resume the pipeline is no longer Failed, so the rule reports <c>false</c> — and the backend
    /// refuses a second resume. This pins that the rule tracks the live state rather than caching a
    /// stale phase, which is what makes the page's button disappear after use.
    /// </summary>
    [Fact]
    public async Task IsResumable_FollowsTheLiveState_AfterASuccessfulResume()
    {
        const string goalId = "parity-post-resume";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, "Review rejected the changes");
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        FailedPipeline(manager, goal, $"copilothive/{goalId}");

        var before = manager.GetPhaseAndCoderBranch(goalId);
        Assert.True(GoalResumeEligibility.IsResumable(goal, before!.Value.Phase, before.Value.CoderBranch));

        var dispatcher = CreateDispatcher(store, manager);
        dispatcher.BranchListerForTest = (repo, ct) => Task.FromResult(new List<string> { $"copilothive/{goalId}" });
        Assert.True(await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken));

        var after = manager.GetPhaseAndCoderBranch(goalId);
        Assert.NotNull(after);
        Assert.NotEqual(GoalPhase.Failed, after!.Value.Phase);
        Assert.False(GoalResumeEligibility.IsResumable(goal, after.Value.Phase, after.Value.CoderBranch));
        Assert.False(await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A goal that is not Failed at all is refused by the backend while the rule reports the same
    /// answer — the goal-level gate is shared, not re-implemented per caller.
    /// </summary>
    [Fact]
    public async Task IsResumable_MatchesResumeGoalAsync_ForANonFailedGoal()
    {
        const string goalId = "parity-not-failed";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, "Review rejected the changes");
        goal.Status = GoalStatus.Completed;
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        FailedPipeline(manager, goal, $"copilothive/{goalId}");

        var observed = manager.GetPhaseAndCoderBranch(goalId);
        Assert.NotNull(observed);
        var predicted = GoalResumeEligibility.IsResumable(goal, observed!.Value.Phase, observed.Value.CoderBranch);
        Assert.False(predicted);

        var dispatcher = CreateDispatcher(store, manager);
        dispatcher.BranchListerForTest = (repo, ct) =>
            throw new InvalidOperationException("a non-Failed goal must never reach branch observation");
        var actual = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.Equal(predicted, actual);
        Assert.Equal(GoalStatus.Completed, goal.Status);
    }

    /// <summary>
    /// NO PIPELINE ANYWHERE is refused by the backend and predicted by the rule from the same
    /// <c>null</c> observation — the null-phase row of the matrix, exercised through the dispatcher.
    /// </summary>
    [Fact]
    public async Task IsResumable_MatchesResumeGoalAsync_WhenNoPipelineExists()
    {
        const string goalId = "parity-no-pipeline";
        var store = new RecordingGoalStore();
        var goal = FailedGoal(goalId, "Exceeded max iterations");
        store.AddGoal(goal);

        var manager = new GoalPipelineManager();
        var observed = manager.GetPhaseAndCoderBranch(goalId);
        Assert.Null(observed);
        var predicted = GoalResumeEligibility.IsResumable(goal, observed?.Phase, observed?.CoderBranch);
        Assert.False(predicted);

        var dispatcher = CreateDispatcher(store, manager);
        var actual = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.Equal(predicted, actual);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a store-backed Failed pipeline whose BOTH task-level retry budgets are exhausted —
    /// the production shape of a goal that failed on "Exceeded max test retries".
    /// </summary>
    private static GoalPipeline ExhaustedRetryPipeline(GoalPipelineManager manager, Goal goal, string? coderBranch)
    {
        var pipeline = manager.CreatePipeline(goal, maxRetries: 3, maxIterations: 3);
        while (pipeline.TestRetryBudget.TryConsume()) { }
        while (pipeline.ReviewRetryBudget.TryConsume()) { }
        pipeline.CoderBranch = coderBranch;
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Coding, 1, 1));
        pipeline.AdvanceTo(GoalPhase.Failed);
        manager.PersistFull(pipeline);
        return pipeline;
    }

    private static string Snapshot(Goal goal, GoalPipeline pipeline) =>
        string.Join('|',
            goal.Status,
            goal.FailureReason,
            goal.CompletedAt?.Ticks.ToString() ?? "null",
            pipeline.Phase,
            pipeline.CoderBranch,
            pipeline.Iteration,
            pipeline.MaxIterations,
            pipeline.IterationBudget.Remaining,
            pipeline.CompletedAt?.Ticks.ToString() ?? "null",
            pipeline.PhaseLog.Count,
            pipeline.Plan?.Phases.Count.ToString() ?? "null",
            pipeline.ActiveTaskId ?? "null");

    // ── Private fakes ────────────────────────────────────────────────────────

    /// <summary>Collecting logger that captures every entry for assertion.</summary>
    private sealed class CapturingLogger : ILogger<GoalDispatcher>
    {
        private readonly Lock _gate = new();

        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
                Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>Worker gateway that always has an idle worker and records every dispatched task.</summary>
    private sealed class CapturingWorkerGateway : IWorkerGateway
    {
        private readonly ConnectedWorker _worker = new()
        {
            Id = "capturing-worker",
            Role = WorkerRole.Unspecified,
            Capabilities = [],
        };

        public List<WorkTask> SentTasks { get; } = [];


        /// <summary>
        /// THE CHECKED CLAIM, mirroring the pool primitive's shape checks: a foreign instance and a
        /// worker that is not idle with a null task are REFUSED. Never an unconditional success.
        /// </summary>
        public bool TryClaimAndActivate(ConnectedWorker expected, WorkTask task, TaskQueue queue)
        {
            if (!ReferenceEquals(expected, _worker) || expected.IsBusy || expected.CurrentTaskId is not null)
                return false;

            queue.Activate(task, expected.Id);
            expected.IsBusy = true;
            expected.CurrentTaskId = task.TaskId;
            expected.CurrentTaskStartedAt = DateTime.UtcNow;
            expected.Role = task.Role;
            expected.CurrentModel = task.Model;
            return true;
        }

        public Task<WorkerTaskSendOutcome> SendTaskAsync(string workerId, WorkTask task, CancellationToken ct = default)
        {
            SentTasks.Add(task);
            return Task.FromResult(WorkerTaskSendOutcome.Published);
        }

        public Task<WorkerTaskSendOutcome> SendTaskAsync(ConnectedWorker worker, WorkTask task, CancellationToken ct = default)
        {
            SentTasks.Add(task);
            return Task.FromResult(WorkerTaskSendOutcome.Published);
        }

        public Task SendCancelAsync(string workerId, string taskId, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SendAgentsUpdateAsync(string workerId, string role, string content, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SendAgentsUpdateAsync(ConnectedWorker worker, string role, string content, CancellationToken ct = default) =>
            Task.CompletedTask;

        public ConnectedWorker? GetIdleWorker() => _worker;

        public IReadOnlyList<ConnectedWorker> GetAllWorkers() => [_worker];

        public void MarkBusy(string workerId, string taskId) { }
    }

    /// <summary>Brain stub with a scriptable plan that records the planning context.</summary>
    private sealed class GateFakeBrain : IDistributedBrain
    {
        public IterationPlan Plan { get; init; } = IterationPlan.Default();

        public List<string?> PlanContexts { get; } = [];

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
        {
            PlanContexts.Add(additionalContext);
            return Task.FromResult(PlanResult.Success(Plan));
        }

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult("summary");

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("proceed"));

        public Task UpdateModelAsync(string model, int? maxContextTokens, ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            Task.CompletedTask;

        public BrainStats? GetStats() => null;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public bool GoalSessionExists(string goalId) => false;

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>In-memory goal store that records every status and goal update.</summary>
    private sealed class RecordingGoalStore : IGoalStore
    {
        private readonly Dictionary<string, Goal> _goals = [];

        public List<(GoalStatus Status, string? Reason)> StatusUpdates { get; } = [];

        public List<string> GoalUpdates { get; } = [];

        public string Name => "restart-recording-store";

        public void AddGoal(Goal goal) => _goals[goal.Id] = goal;

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList());

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult(_goals.GetValueOrDefault(goalId));

        public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
        {
            _goals[goal.Id] = goal;
            return Task.FromResult(goal);
        }

        public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
        {
            GoalUpdates.Add(goal.Id);
            _goals[goal.Id] = goal;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult(_goals.Remove(goalId));

        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IterationSummary>>([]);

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
        {
            StatusUpdates.Add((status, metadata?.FailureReason));
            if (_goals.TryGetValue(goalId, out var goal))
            {
                goal.Status = status;
                if (metadata?.FailureReason is not null)
                    goal.FailureReason = metadata.FailureReason;
            }

            return Task.CompletedTask;
        }

        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
            Task.FromResult(release);

        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult<Release?>(null);

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Release>>([]);

        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(
            int? limit = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>([]);
    }
}
