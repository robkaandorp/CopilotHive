using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Dashboard;

/// <summary>
/// Tests for <see cref="GoalDetailInfo.CanResume"/> — the flag the Goal Detail page's resume button
/// reads — as computed by <see cref="DashboardStateService.GetGoalDetail"/> with the SHARED rule
/// <see cref="GoalResumeEligibility.IsResumable"/>.
/// <para>
/// The central scenario is the ORCHESTRATOR-RESTART shape: the goal's Failed pipeline exists ONLY in
/// the database (LoadActivePipelines skips terminal phases), which is exactly the case the old
/// in-memory-only detail read got wrong. The read must resolve the store row WITHOUT admitting the
/// pipeline into memory.
/// </para>
/// </summary>
public sealed class GoalDetailCanResumeTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _goalStore;
    private readonly PipelineStore _pipelineStore;

    /// <summary>Initialises the goal and pipeline stores over one in-memory SQLite database.</summary>
    public GoalDetailCanResumeTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _goalStore = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
        _pipelineStore = new PipelineStore(_dbContext, NullLogger<PipelineStore>.Instance);
    }

    /// <inheritdoc />
    public void Dispose() => _dbContext.Dispose();

    // ── REAL-dispatcher wiring (parity vectors only) ─────────────────────────

    /// <summary>The repository every parity goal references, configured for the dispatcher.</summary>
    private const string DispatcherRepoName = "test-repo";

    /// <summary>
    /// A bounded per-repository branch-observation deadline, mirroring the other resume fixtures. The
    /// parity vectors never reach the observation (they refuse before planning or return a listed
    /// branch), so this only keeps a stray observation from stalling the run.
    /// </summary>
    private static readonly TimeSpan TestResumeTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Builds a REAL <see cref="GoalDispatcher"/> over the SAME goal store the dashboard read used, so
    /// the parity vectors compare the two against one shared authoritative state. The Brain is the
    /// shared <see cref="NoOpDistributedBrain"/>: for the vector that resumes, planning succeeds with
    /// the default plan and no LLM I/O happens.
    /// </summary>
    private static GoalDispatcher CreateDispatcher(IGoalStore goalStore, GoalPipelineManager manager)
    {
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);
        var dispatcher = new GoalDispatcher(
            goalManager,
            manager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            new NoOpDistributedBrain(),
            config: new HiveConfigFile
            {
                Repositories =
                [
                    new RepositoryConfig
                    {
                        Name = DispatcherRepoName,
                        Url = "https://github.com/test/repo.git",
                        DefaultBranch = "main",
                    },
                ],
                Workers =
                {
                    ["coder"] = new WorkerConfig { Model = "coder-model" },
                },
            },
            goalStore: goalStore);
        dispatcher.ResumeTimeout = TestResumeTimeout;
        return dispatcher;
    }

    /// <summary>
    /// Builds a REAL <see cref="GoalDispatcher"/> with NO goal store configured — the backend state
    /// whose <c>goalStore is null</c> gate refuses EVERY resume unconditionally. Nothing is registered
    /// as a goal source either, exactly as a store-less host would wire it.
    /// </summary>
    private static GoalDispatcher CreateDispatcherWithoutGoalStore(GoalPipelineManager manager)
    {
        var dispatcher = new GoalDispatcher(
            new GoalManager(),
            manager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            new NoOpDistributedBrain(),
            config: new HiveConfigFile
            {
                Repositories =
                [
                    new RepositoryConfig
                    {
                        Name = DispatcherRepoName,
                        Url = "https://github.com/test/repo.git",
                        DefaultBranch = "main",
                    },
                ],
                Workers =
                {
                    ["coder"] = new WorkerConfig { Model = "coder-model" },
                },
            },
            goalStore: null);
        dispatcher.ResumeTimeout = TestResumeTimeout;
        return dispatcher;
    }

    /// <summary>
    /// Builds the service over a FRESH manager (so nothing is registered in memory) that shares the
    /// persistent stores — the post-restart wiring.
    /// </summary>
    private DashboardStateService CreateService(GoalPipelineManager manager) =>
        new(
            new WorkerPool(),
            manager,
            new DashboardLogSink(),
            new ProgressLog(),
            goalStore: _goalStore);

    private async Task<Goal> SeedFailedGoalAsync(string goalId, string reason, CancellationToken ct)
    {
        var goal = new Goal
        {
            Id = goalId,
            Description = "Goal detail resume flag goal",
            Status = GoalStatus.Failed,
            FailureReason = reason,
        };
        await _goalStore.CreateGoalAsync(goal, ct);
        return goal;
    }

    /// <summary>
    /// Seeds a FAILED pipeline carrying <paramref name="coderBranch"/> DIRECTLY into the store,
    /// bypassing the manager, so the pipeline exists nowhere in memory — the restart shape.
    /// </summary>
    private void SeedStoreOnlyFailedPipeline(Goal goal, string? coderBranch)
    {
        var pipeline = new GoalPipeline(goal, maxRetries: 3, maxIterations: 3);
        pipeline.CoderBranch = coderBranch;
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Coding, 1, 1));
        pipeline.AdvanceTo(GoalPhase.Failed);
        _pipelineStore.SavePipeline(pipeline);
    }

    /// <summary>
    /// THE REGRESSION VECTOR: a Failed goal whose reason is "Worker failed: …" — which contains no
    /// "iteration" text, so the OLD page condition hid the button — and whose Failed pipeline with
    /// the canonical coder branch exists ONLY in the store. The detail must report
    /// <c>CanResume = true</c>, and the read must not have admitted the pipeline into memory.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_StoreOnlyFailedPipelineWithWorkerFailure_SetCanResumeTrue_WithoutAdmitting()
    {
        const string goalId = "restart-worker-failed";
        var goal = await SeedFailedGoalAsync(goalId, "Worker failed: build error in Coding", TestContext.Current.CancellationToken);
        SeedStoreOnlyFailedPipeline(goal, $"copilothive/{goalId}");

        var manager = new GoalPipelineManager(_pipelineStore);
        // PREMISE: the pipeline is not in memory before the read.
        Assert.Null(manager.GetByGoalId(goalId));
        Assert.Empty(manager.GetAllPipelines());

        using var service = CreateService(manager);
        var detail = await service.GetGoalDetail(goalId);

        Assert.NotNull(detail);
        Assert.True(detail!.CanResume);
        Assert.Equal(GoalStatus.Failed, detail.Status);
        Assert.Equal("Worker failed: build error in Coding", detail.FailureReason);

        // NO MEMORY ADMISSION: the read is side-effect-free — neither the pipeline registry nor the
        // list of pipelines gains an entry.
        Assert.Null(manager.GetByGoalId(goalId));
        Assert.Empty(manager.GetAllPipelines());
    }

    /// <summary>
    /// A cancelled goal is never resumable, even when its Failed pipeline (with the canonical branch)
    /// is present in the store only — the cancellation predicate is shared with the backend.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_StoreOnlyFailedPipelineCancelledGoal_SetCanResumeFalse_WithoutAdmitting()
    {
        const string goalId = "restart-cancelled";
        var goal = await SeedFailedGoalAsync(goalId, "Cancelled by user", TestContext.Current.CancellationToken);
        SeedStoreOnlyFailedPipeline(goal, $"copilothive/{goalId}");

        var manager = new GoalPipelineManager(_pipelineStore);

        using var service = CreateService(manager);
        var detail = await service.GetGoalDetail(goalId);

        Assert.NotNull(detail);
        Assert.False(detail!.CanResume);
        Assert.Null(manager.GetByGoalId(goalId));
        Assert.Empty(manager.GetAllPipelines());
    }

    /// <summary>
    /// The cancellation predicate is EQUALITY: a suffixed reason ("Cancelled by user (test)") is a
    /// different failure, so a store-only canonical-branch pipeline is resumable for it.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_StoreOnlyPipelineWithSuffixedCancellationReason_SetCanResumeTrue()
    {
        const string goalId = "restart-cancel-suffix";
        var goal = await SeedFailedGoalAsync(goalId, "Cancelled by user (test)", TestContext.Current.CancellationToken);
        SeedStoreOnlyFailedPipeline(goal, $"copilothive/{goalId}");

        var manager = new GoalPipelineManager(_pipelineStore);

        using var service = CreateService(manager);
        var detail = await service.GetGoalDetail(goalId);

        Assert.NotNull(detail);
        Assert.True(detail!.CanResume);
        Assert.Null(manager.GetByGoalId(goalId));
    }

    /// <summary>
    /// A store-only FAILED pipeline whose branch is NOT canonical is refused — the same branch
    /// invariant the backend enforces, observed through the dashboard read.
    /// </summary>
    [Theory]
    [InlineData("copilothive/some-other-goal")]
    [InlineData("COPILOTHIVE/restart-branch-mismatch")]
    public async Task GetGoalDetail_StoreOnlyPipelineWithNonCanonicalBranch_SetCanResumeFalse(string branch)
    {
        const string goalId = "restart-branch-mismatch";
        var goal = await SeedFailedGoalAsync(goalId, "Review rejected the changes", TestContext.Current.CancellationToken);
        SeedStoreOnlyFailedPipeline(goal, branch);

        var manager = new GoalPipelineManager(_pipelineStore);

        using var service = CreateService(manager);
        var detail = await service.GetGoalDetail(goalId);

        Assert.NotNull(detail);
        Assert.False(detail!.CanResume);
        Assert.Null(manager.GetByGoalId(goalId));
    }

    /// <summary>
    /// A store-only FAILED pipeline with NO branch resumes ONLY on iteration exhaustion: the same
    /// "Worker failed: …" reason that is resumable WITH a canonical branch is refused branchless.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_StoreOnlyBranchlessPipeline_ResumesOnlyOnExhaustion()
    {
        const string workerFailedGoalId = "restart-branchless-worker";
        var workerFailedGoal = await SeedFailedGoalAsync(workerFailedGoalId, "Worker failed: build error in Coding", TestContext.Current.CancellationToken);
        SeedStoreOnlyFailedPipeline(workerFailedGoal, coderBranch: null);

        const string exhaustedGoalId = "restart-branchless-exhausted";
        var exhaustedGoal = await SeedFailedGoalAsync(exhaustedGoalId, "Exceeded max iterations", TestContext.Current.CancellationToken);
        SeedStoreOnlyFailedPipeline(exhaustedGoal, coderBranch: null);

        var manager = new GoalPipelineManager(_pipelineStore);

        using var service = CreateService(manager);
        Assert.False((await service.GetGoalDetail(workerFailedGoalId))!.CanResume);
        Assert.True((await service.GetGoalDetail(exhaustedGoalId))!.CanResume);

        Assert.Null(manager.GetByGoalId(workerFailedGoalId));
        Assert.Null(manager.GetByGoalId(exhaustedGoalId));
        Assert.Empty(manager.GetAllPipelines());
    }

    /// <summary>
    /// A goal with NO pipeline anywhere can never be resumed, whatever its reason and status — the
    /// null-phase row, read through the dashboard.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_NoPipelineAnywhere_SetCanResumeFalse()
    {
        const string goalId = "restart-no-pipeline";
        await SeedFailedGoalAsync(goalId, "Exceeded max iterations", TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager(_pipelineStore);

        using var service = CreateService(manager);
        var detail = await service.GetGoalDetail(goalId);

        Assert.NotNull(detail);
        Assert.False(detail!.CanResume);
    }

    /// <summary>
    /// A FAILED goal whose pipeline is IN MEMORY and still Failed reports <c>CanResume = true</c>:
    /// the in-memory pipeline is authoritative, and this is the shape the ORIGINAL page supported.
    /// </summary>
    /// <remarks>
    /// PARITY-CORRECT BY CONSTRUCTION: the fixture supplies a goal store holding the AUTHORITATIVE
    /// Failed goal (seeded by <see cref="SeedFailedGoalAsync"/>, which persists <c>GoalStatus.Failed</c>)
    /// — the "store with the authoritative Failed goal" route rather than a no-store state — so the
    /// true-expectation is one the backend really accepts. The stored-status premise is asserted below
    /// instead of assumed.
    /// </remarks>
    [Fact]
    public async Task GetGoalDetail_InMemoryFailedPipeline_SetCanResumeTrue()
    {
        const string goalId = "restart-in-memory";
        var goal = await SeedFailedGoalAsync(goalId, "Review rejected the changes", TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager(_pipelineStore);
        var pipeline = manager.CreatePipeline(goal, maxRetries: 3, maxIterations: 3);
        pipeline.CoderBranch = $"copilothive/{goalId}";
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Coding, 1, 1));
        pipeline.AdvanceTo(GoalPhase.Failed);
        manager.PersistFull(pipeline);

        using var service = CreateService(manager);

        // PREMISE (the parity input): the AUTHORITATIVE stored goal really is Failed, so a true flag is
        // what the backend would answer rather than a phase-derived accident.
        var authoritativelyStored = await _goalStore.GetGoalAsync(goalId, TestContext.Current.CancellationToken);
        Assert.NotNull(authoritativelyStored);
        Assert.Equal(GoalStatus.Failed, authoritativelyStored!.Status);

        var detail = await service.GetGoalDetail(goalId);

        Assert.NotNull(detail);
        Assert.True(detail!.CanResume);
        Assert.Same(pipeline, manager.GetByGoalId(goalId));
    }

    // ── The status-disagreement boundary ─────────────────────────────────────

    /// <summary>
    /// Seeds a LIVE (in-memory) pipeline in the <see cref="GoalPhase.Failed"/> phase carrying the
    /// canonical coder branch for <paramref name="goal"/> — the exact production boundary where the
    /// pipeline reaches Failed BEFORE the asynchronous goal finalization persists that status.
    /// </summary>
    private GoalPipeline SeedLiveFailedPipeline(GoalPipelineManager manager, Goal goal)
    {
        var pipeline = manager.CreatePipeline(goal, maxRetries: 3, maxIterations: 3);
        pipeline.CoderBranch = $"copilothive/{goal.Id}";
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Coding, 1, 1));
        pipeline.AdvanceTo(GoalPhase.Failed);
        manager.PersistFull(pipeline);
        return pipeline;
    }

    /// <summary>
    /// THE STATUS-DISAGREEMENT VECTOR (the lifecycle boundary): the STORED goal status is a failable
    /// non-Failed value while the LIVE pipeline is already Failed with the canonical branch.
    /// <see cref="DashboardStateService.GetSnapshot"/> overwrites the snapshot goal's Status from the
    /// pipeline phase (Failed), so evaluating eligibility against THAT copy would report
    /// <c>CanResume = true</c> — while the backend reads the stored non-Failed status and refuses.
    /// The flag must therefore be FALSE, and a REAL dispatcher must agree.
    /// </summary>
    /// <remarks>
    /// The assertions are paired so a one-sided fix fails: the dashboard flag and the dispatcher's
    /// answer must MATCH each other and both be false, with an INDEPENDENT fresh store read proving
    /// the persisted row still holds the non-Failed status (so the refusal is caused by the stored
    /// status, not by a fixture that accidentally mutated it).
    /// </remarks>
    [Theory]
    [InlineData(GoalStatus.InProgress)]
    [InlineData(GoalStatus.Completed)]
    public async Task GetGoalDetail_StoredStatusDisagreesWithLiveFailedPipeline_SetCanResumeFalse_AndDispatcherRefuses(
        GoalStatus storedStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        var goalId = "status-disagreement";
        var goal = new Goal
        {
            Id = goalId,
            Description = "Stored status disagrees with the live pipeline phase",
            Status = storedStatus,
            FailureReason = null,
            RepositoryNames = [DispatcherRepoName],
        };
        await _goalStore.CreateGoalAsync(goal, ct);

        var manager = new GoalPipelineManager(_pipelineStore);
        SeedLiveFailedPipeline(manager, goal);
        // PREMISE: the pipeline really is live and really is Failed.
        Assert.Equal(GoalPhase.Failed, manager.GetByGoalId(goalId)!.Phase);

        using var service = CreateService(manager);

        // THE LOAD-BEARING PREMISE: GetSnapshot ITSELF derives the snapshot goal's Status from the
        // pipeline phase, so the selected snapshot goal really does read Failed while the STORED goal
        // reads the non-Failed value. Obtained directly from GetSnapshot — NOT via the detail view,
        // whose Status GoalDetailViewBuilder derives independently from pipeline.Phase and which
        // would therefore still read Failed even if GetSnapshot stopped deriving it.
        var snap = await service.GetSnapshot();
        var snapshotGoal = Assert.Single(snap.Goals, g => g.Id == goalId);
        Assert.Equal(GoalStatus.Failed, snapshotGoal.Status);
        // …and the disagreement is real: the stored row this fixture wrote is NOT Failed.
        Assert.NotEqual(GoalStatus.Failed, storedStatus);

        var detail = await service.GetGoalDetail(goalId);

        Assert.NotNull(detail);
        // The BUILDER-derived display status is also Failed (its own pipeline.Phase derivation) —
        // retained as the display pin, independent of the snapshot assertion above.
        Assert.Equal(GoalStatus.Failed, detail!.Status);
        Assert.False(detail.CanResume);

        // REAL-DISPATCHER PARITY: the stored non-Failed status is what ResumeGoalAsync reads.
        var dispatcher = CreateDispatcher(_goalStore, manager);
        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, ct);

        Assert.False(resumed);

        // INDEPENDENT STORED-STATUS READBACK: a fresh store read (the same no-tracking channel the
        // dispatcher uses) shows the refusal was caused by the PERSISTED status.
        var reread = await _goalStore.GetGoalAsync(goalId, ct);
        Assert.NotNull(reread);
        Assert.Equal(storedStatus, reread!.Status);
    }

    /// <summary>
    /// THE INVERSE-CONSISTENT VECTOR: the SAME fixture family with the stored status back in
    /// agreement (Failed) must yield <c>CanResume = true</c> AND a dispatcher that ACCEPTS the
    /// resume — so the fix discriminates between the two rows instead of blanket-disabling the
    /// button.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_StoredFailedStatusWithLiveFailedPipeline_SetCanResumeTrue_AndDispatcherAccepts()
    {
        var ct = TestContext.Current.CancellationToken;
        const string goalId = "status-agreement";
        var goal = new Goal
        {
            Id = goalId,
            Description = "Stored status agrees with the live pipeline phase",
            Status = GoalStatus.Failed,
            FailureReason = "Review rejected the changes",
            RepositoryNames = [DispatcherRepoName],
        };
        await _goalStore.CreateGoalAsync(goal, ct);

        var manager = new GoalPipelineManager(_pipelineStore);
        SeedLiveFailedPipeline(manager, goal);

        using var service = CreateService(manager);
        var detail = await service.GetGoalDetail(goalId);

        Assert.NotNull(detail);
        Assert.Equal(GoalStatus.Failed, detail!.Status);
        Assert.True(detail.CanResume);

        var dispatcher = CreateDispatcher(_goalStore, manager);
        dispatcher.BranchListerForTest = (_, _) =>
            Task.FromResult(new List<string> { $"copilothive/{goalId}" });
        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, ct);

        Assert.True(resumed);
    }

    // ── The fail-closed absence states ────────────────────────────────────────

    /// <summary>
    /// THE NO-STORE STATE, FAIL CLOSED: with NO goal store configured the dashboard cannot read an
    /// authoritative status, and <see cref="GoalDispatcher.ResumeGoalAsync"/> refuses EVERY goal at its
    /// <c>goalStore is null</c> gate. CanResume must therefore be FALSE for a live Failed pipeline with
    /// the canonical branch — the old phase-derived fallback showed a button the backend would refuse.
    /// The same fixture with a non-canonical branch is refused too, and neither read admits anything
    /// beyond the pre-existing live pipelines.
    /// </summary>
    /// <remarks>
    /// The assertion is a PARITY one, not a blanket expectation: a REAL dispatcher built with no goal
    /// store refuses the very same goal, so the flag and the backend agree in this state.
    /// </remarks>
    [Fact]
    public async Task GetGoalDetail_NoStoreConfigured_FailsClosed_AndStorelessDispatcherRefuses()
    {
        var manager = new GoalPipelineManager();

        const string canonicalGoalId = "nostore-canonical";
        var canonicalGoal = new Goal
        {
            Id = canonicalGoalId,
            Description = "No-store goal, canonical branch",
            Status = GoalStatus.InProgress,
        };
        var canonicalPipeline = manager.CreatePipeline(canonicalGoal, maxRetries: 3, maxIterations: 3);
        canonicalPipeline.CoderBranch = $"copilothive/{canonicalGoalId}";
        canonicalPipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Coding, 1, 1));
        canonicalPipeline.AdvanceTo(GoalPhase.Failed);

        const string mismatchedGoalId = "nostore-mismatched";
        var mismatchedGoal = new Goal
        {
            Id = mismatchedGoalId,
            Description = "No-store goal, non-canonical branch",
            Status = GoalStatus.InProgress,
        };
        var mismatchedPipeline = manager.CreatePipeline(mismatchedGoal, maxRetries: 3, maxIterations: 3);
        mismatchedPipeline.CoderBranch = "copilothive/some-other-goal";
        mismatchedPipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Coding, 1, 1));
        mismatchedPipeline.AdvanceTo(GoalPhase.Failed);

        using var service = new DashboardStateService(
            new WorkerPool(),
            manager,
            new DashboardLogSink(),
            new ProgressLog(),
            goalStore: null);

        var canonicalDetail = await service.GetGoalDetail(canonicalGoalId);
        var mismatchedDetail = await service.GetGoalDetail(mismatchedGoalId);

        Assert.NotNull(canonicalDetail);
        // The DISPLAY still shows the phase-derived Failed (nothing rendered changed)…
        Assert.Equal(GoalStatus.Failed, canonicalDetail!.Status);
        // …but the button is FAIL-CLOSED: no store means the backend can never accept this resume.
        Assert.False(canonicalDetail.CanResume);

        Assert.NotNull(mismatchedDetail);
        Assert.Equal(GoalStatus.Failed, mismatchedDetail!.Status);
        Assert.False(mismatchedDetail.CanResume);

        // REAL-DISPATCHER PARITY: a store-less dispatcher refuses the canonical-branch goal at its
        // goalStore-null gate — the exact refusal the fail-closed flag mirrors.
        var dispatcher = CreateDispatcherWithoutGoalStore(manager);
        Assert.False(await dispatcher.ResumeGoalAsync(
            canonicalGoalId, 5, TestContext.Current.CancellationToken));

        // NO ADMISSION BEYOND THE LIVE PIPELINES: both pipelines were registered by the fixture —
        // the read adds nothing and removes nothing.
        Assert.Same(canonicalPipeline, manager.GetByGoalId(canonicalGoalId));
        Assert.Same(mismatchedPipeline, manager.GetByGoalId(mismatchedGoalId));
        Assert.Equal(2, manager.GetAllPipelines().Count);
    }

    /// <summary>
    /// THE VANISHED-GOAL STATE, FAIL CLOSED: the goal was deleted from the store AFTER the live
    /// pipeline was created, so <c>GetGoalAsync</c> answers <c>null</c> while the snapshot still carries
    /// the pipeline-merged goal. <see cref="GoalDispatcher.ResumeGoalAsync"/> refuses such a goal
    /// unconditionally (<c>goal is null</c>), so CanResume must be FALSE even though the display still
    /// reads the phase-derived Failed. The disappearance premise and the no-admission assertions are
    /// retained.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_GoalVanishedFromStore_FailsClosed_AndDispatcherRefuses()
    {
        const string goalId = "vanished-from-store";
        var goal = new Goal
        {
            Id = goalId,
            Description = "Goal deleted from the store while its pipeline is still live",
            Status = GoalStatus.InProgress,
            FailureReason = null,
            RepositoryNames = [DispatcherRepoName],
        };
        await _goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager(_pipelineStore);
        // THE FIXTURE'S OWN INSTANCE, captured BEFORE the read so the post-read lookup can be compared
        // against this immutable reference (never against itself).
        var seededPipeline = SeedLiveFailedPipeline(manager, goal);
        // PREMISE: the pipeline really is live and really is Failed.
        Assert.Equal(GoalPhase.Failed, manager.GetByGoalId(goalId)!.Phase);

        var deleted = await _goalStore.DeleteGoalAsync(goalId, TestContext.Current.CancellationToken);
        Assert.True(deleted);
        // THE DISAPPEARANCE PREMISE: the store no longer has the goal, so the authoritative read
        // answers null and the fail-closed branch is the one under test.
        Assert.Null(await _goalStore.GetGoalAsync(goalId, TestContext.Current.CancellationToken));

        using var service = CreateService(manager);
        var detail = await service.GetGoalDetail(goalId);

        Assert.NotNull(detail);
        // The DISPLAY still reads the phase-derived Failed (the snapshot goal survives via the pipeline
        // merge) — the page renders as before…
        Assert.Equal(GoalStatus.Failed, detail!.Status);
        // …but with no authoritative goal the button is FAIL-CLOSED.
        Assert.False(detail.CanResume);

        // REAL-DISPATCHER PARITY: over the SAME store, whose GetGoalAsync answers null for this goal,
        // ResumeGoalAsync refuses.
        var dispatcher = CreateDispatcher(_goalStore, manager);
        Assert.False(await dispatcher.ResumeGoalAsync(
            goalId, 5, TestContext.Current.CancellationToken));

        // NO ADMISSION: the live pipeline is the fixture's own instance — the read neither replaced it
        // nor added another.
        Assert.Same(seededPipeline, manager.GetByGoalId(goalId));
        Assert.Single(manager.GetAllPipelines());
    }

    /// <summary>
    /// A COMPLETED goal with a Done pipeline is never resumable — the goal-level gate, through the
    /// dashboard.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_CompletedGoalWithDonePipeline_SetCanResumeFalse()
    {
        const string goalId = "restart-completed";
        var goal = new Goal
        {
            Id = goalId,
            Description = "Completed goal",
            Status = GoalStatus.Completed,
        };
        await _goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager(_pipelineStore);
        var pipeline = manager.CreatePipeline(goal, maxRetries: 3, maxIterations: 3);
        pipeline.CoderBranch = $"copilothive/{goalId}";
        pipeline.AdvanceTo(GoalPhase.Done);
        manager.PersistFull(pipeline);

        using var service = CreateService(manager);
        var detail = await service.GetGoalDetail(goalId);

        Assert.NotNull(detail);
        Assert.False(detail!.CanResume);
    }
}
