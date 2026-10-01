using CopilotHive.Goals;
using CopilotHive.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using SharpCoder;

namespace CopilotHive.Tests.Goals;

/// <summary>
/// The per-goal Brain / pre-execution-review usage persistence contract:
/// <see cref="Goal.BrainUsage"/> and <see cref="Goal.ReviewUsage"/> round-trip through the
/// <c>brain_usage</c> / <c>review_usage</c> JSON columns, and
/// <see cref="IGoalStore.AddGoalUsageAsync"/> MERGES one completed call's usage into the stored value
/// for its <see cref="GoalUsageKind"/> — per <c>(source, model)</c>, keeping every counter and the
/// estimated breakdown — without ever turning "nothing recorded" (<c>null</c>) into an empty summary
/// or letting a stale in-memory <see cref="Goal"/> instance overwrite the accumulated value.
/// </summary>
public sealed class GoalUsagePersistenceTests : IDisposable
{
    /// <summary>
    /// A HANG GUARD only — never a deadline the assertions depend on. Every wait in this class is on a
    /// rendezvous whose completion is causally required by the test's own release, so a correct run
    /// settles immediately; a stuck one fails here instead of hanging the suite.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(20);

    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;

    public GoalUsagePersistenceTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
    }

    public void Dispose() => _dbContext.Dispose();

    // ── Fixtures ──────────────────────────────────────────────────────────

    private static Goal MakeGoal(string id = "usage-goal") => new()
    {
        Id = id,
        Description = "Record Brain and review usage",
        Status = GoalStatus.Pending,
        Priority = GoalPriority.Normal,
        CreatedAt = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc),
    };

    private static UsageSummary Summary(params UsageEntry[] entries) => new(entries);

    private static UsageEntry Entry(UsageSource source, string? model, TokenUsage usage) =>
        new(source, model, usage);

    /// <summary>Reads back the one entry of the given <c>(source, model)</c> bucket.</summary>
    private static UsageEntry Bucket(UsageSummary? usage, UsageSource source, string? model)
    {
        Assert.NotNull(usage);
        return Assert.Single(usage!.Entries, e => e.Source == source && e.Model == model);
    }

    // ── Defaults ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateGoalAsync_NewGoal_LeavesBothUsageColumnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal(), ct);

        var goal = await _store.GetGoalAsync("usage-goal", ct);

        Assert.NotNull(goal);
        // Nothing recorded is `null` — never an empty summary with zero entries.
        Assert.Null(goal!.BrainUsage);
        Assert.Null(goal.ReviewUsage);
    }

    // ── AddGoalUsageAsync: merge, kinds, no-ops ───────────────────────────

    /// <summary>
    /// Two adds of the SAME kind are summed in one <c>(source, model)</c> bucket: every token count
    /// and every call counter adds up, and a category that only ONE of the two adds reported stays
    /// partial (its reported-calls counter is summed, not overwritten). Distinct bucket keys stay
    /// separate entries, including a <c>null</c> model.
    /// </summary>
    [Fact]
    public async Task AddGoalUsageAsync_TwoAddsOfSameKind_SumsPerSourceModelAndAddsEveryCounter()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal(), ct);

        // Add 1: cached input AND reasoning reported on every call; a second bucket with a null model.
        await _store.AddGoalUsageAsync("usage-goal", GoalUsageKind.Brain, Summary(
            Entry(UsageSource.Agent, "model-a", new TokenUsage
            {
                InputTokens = 100,
                OutputTokens = 10,
                CachedInputTokens = 5,
                ReasoningTokens = 3,
                Calls = 2,
                CachedInputReportedCalls = 2,
                ReasoningReportedCalls = 2,
                Estimated = new EstimatedTokenBreakdown
                {
                    SystemPrompt = 1,
                    ToolDefinitions = 2,
                    UserText = 3,
                    AssistantText = 4,
                    ToolCalls = 5,
                    ToolResults = 6,
                    Reasoning = 7,
                    Images = 8,
                    OutputText = 9,
                    OutputToolCalls = 10,
                    OutputReasoning = 11,
                    InputEstimatedCalls = 12,
                    OutputEstimatedCalls = 13,
                },
            }),
            Entry(UsageSource.SubAgent, null, new TokenUsage { InputTokens = 1_000, OutputTokens = 100, Calls = 4 })), ct);

        // Add 2: cached input reported on ONE of its three calls only, reasoning not reported at all —
        // so the merged category must stay PARTIAL rather than being replaced by this add's counters.
        await _store.AddGoalUsageAsync("usage-goal", GoalUsageKind.Brain, Summary(
            Entry(UsageSource.Agent, "model-a", new TokenUsage
            {
                InputTokens = 400,
                OutputTokens = 40,
                CachedInputTokens = 50,
                Calls = 3,
                CachedInputReportedCalls = 1,
                ReasoningReportedCalls = 0,
            })), ct);

        var goal = await _store.GetGoalAsync("usage-goal", ct);
        Assert.NotNull(goal);
        Assert.NotNull(goal!.BrainUsage);

        var merged = Bucket(goal.BrainUsage, UsageSource.Agent, "model-a").Usage;
        Assert.Equal(500, merged.InputTokens);
        Assert.Equal(50, merged.OutputTokens);
        Assert.Equal(55, merged.CachedInputTokens);
        Assert.Equal(3, merged.ReasoningTokens);
        Assert.Equal(5, merged.Calls);
        Assert.Equal(3, merged.CachedInputReportedCalls);
        Assert.Equal(2, merged.ReasoningReportedCalls);

        // The estimated breakdown of the one bucket that carried it is preserved verbatim.
        Assert.Equal(1, merged.Estimated.SystemPrompt);
        Assert.Equal(8, merged.Estimated.Images);
        Assert.Equal(11, merged.Estimated.OutputReasoning);
        Assert.Equal(12, merged.Estimated.InputEstimatedCalls);
        Assert.Equal(13, merged.Estimated.OutputEstimatedCalls);

        // The third add's bucket key (SubAgent, null model) is a SEPARATE entry, not merged into
        // (Agent, model-a) — and its null model survived the round trip as null.
        Assert.Equal(2, goal.BrainUsage!.Entries.Count);
        var subAgent = Bucket(goal.BrainUsage, UsageSource.SubAgent, null).Usage;
        Assert.Equal(1_000, subAgent.InputTokens);
        Assert.Equal(100, subAgent.OutputTokens);
        Assert.Equal(4, subAgent.Calls);
    }

    /// <summary>
    /// The merged summary keeps a DETERMINISTIC entry order: the already-stored buckets stay in their
    /// existing order and a bucket seen for the first time is appended after them. The order comes
    /// straight from the merge argument order (stored value first, new usage second) and is what every
    /// rendering of the entries follows.
    /// </summary>
    [Fact]
    public async Task AddGoalUsageAsync_EntryOrder_KeepsStoredBucketsAndAppendsNewOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal(), ct);

        await _store.AddGoalUsageAsync("usage-goal", GoalUsageKind.Brain, Summary(
            Entry(UsageSource.SubAgent, "first-stored", new TokenUsage { InputTokens = 1, Calls = 1 }),
            Entry(UsageSource.Agent, "second-stored", new TokenUsage { InputTokens = 2, Calls = 1 })), ct);

        await _store.AddGoalUsageAsync("usage-goal", GoalUsageKind.Brain, Summary(
            Entry(UsageSource.Compaction, "appended", new TokenUsage { InputTokens = 3, Calls = 1 }),
            Entry(UsageSource.SubAgent, "first-stored", new TokenUsage { InputTokens = 10, Calls = 1 })), ct);

        var goal = await _store.GetGoalAsync("usage-goal", ct);
        var entries = goal!.BrainUsage!.Entries;
        Assert.Equal(3, entries.Count);
        Assert.Equal("first-stored", entries[0].Model);
        Assert.Equal("second-stored", entries[1].Model);
        Assert.Equal("appended", entries[2].Model);
        Assert.Equal(11, entries[0].Usage.InputTokens);
    }

    /// <summary>
    /// The two kinds accumulate in SEPARATE columns: adding Brain usage never touches
    /// <see cref="Goal.ReviewUsage"/> and vice versa, in both directions.
    /// </summary>
    [Fact]
    public async Task AddGoalUsageAsync_BrainAndReview_AreStoredIndependently()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal(), ct);

        var brain = Summary(Entry(UsageSource.Agent, "brain-model", new TokenUsage { InputTokens = 111, Calls = 1 }));
        var review = Summary(Entry(UsageSource.Agent, "review-model", new TokenUsage { InputTokens = 222, Calls = 1 }));

        await _store.AddGoalUsageAsync("usage-goal", GoalUsageKind.Brain, brain, ct);
        var afterBrain = await _store.GetGoalAsync("usage-goal", ct);
        Assert.Equal(111, Bucket(afterBrain!.BrainUsage, UsageSource.Agent, "brain-model").Usage.InputTokens);
        Assert.Null(afterBrain.ReviewUsage);

        await _store.AddGoalUsageAsync("usage-goal", GoalUsageKind.PreExecutionReview, review, ct);
        var afterReview = await _store.GetGoalAsync("usage-goal", ct);
        // The review add did not disturb the Brain column, and the Brain add did not create a review value.
        Assert.Equal(111, Bucket(afterReview!.BrainUsage, UsageSource.Agent, "brain-model").Usage.InputTokens);
        Assert.Single(afterReview.BrainUsage!.Entries);
        Assert.Equal(222, Bucket(afterReview.ReviewUsage, UsageSource.Agent, "review-model").Usage.InputTokens);
        Assert.Single(afterReview.ReviewUsage!.Entries);

        // …and the direction review-then-brain.
        await _store.AddGoalUsageAsync("usage-goal", GoalUsageKind.PreExecutionReview, review, ct);
        var afterSecondReview = await _store.GetGoalAsync("usage-goal", ct);
        Assert.Equal(444, Bucket(afterSecondReview!.ReviewUsage, UsageSource.Agent, "review-model").Usage.InputTokens);
        Assert.Equal(111, Bucket(afterSecondReview.BrainUsage, UsageSource.Agent, "brain-model").Usage.InputTokens);
    }

    /// <summary>
    /// An EMPTY summary records nothing: it must not turn <c>null</c> into an empty summary, and it
    /// must not disturb a value that is already recorded.
    /// </summary>
    [Fact]
    public async Task AddGoalUsageAsync_EmptySummary_IsANoOpThatKeepsNullAndExistingValues()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal(), ct);

        await _store.AddGoalUsageAsync("usage-goal", GoalUsageKind.Brain, new UsageSummary(), ct);
        await _store.AddGoalUsageAsync("usage-goal", GoalUsageKind.PreExecutionReview, new UsageSummary(), ct);

        var afterEmpty = await _store.GetGoalAsync("usage-goal", ct);
        Assert.Null(afterEmpty!.BrainUsage);
        Assert.Null(afterEmpty.ReviewUsage);

        await _store.AddGoalUsageAsync("usage-goal", GoalUsageKind.Brain,
            Summary(Entry(UsageSource.Agent, "brain-model", new TokenUsage { InputTokens = 42, Calls = 7 })), ct);
        await _store.AddGoalUsageAsync("usage-goal", GoalUsageKind.Brain, new UsageSummary(), ct);

        var afterEmptyOnExisting = await _store.GetGoalAsync("usage-goal", ct);
        var stored = Bucket(afterEmptyOnExisting!.BrainUsage, UsageSource.Agent, "brain-model").Usage;
        Assert.Equal(42, stored.InputTokens);
        Assert.Equal(7, stored.Calls);
    }

    /// <summary>
    /// Usage recording must never fail the Brain call or the review it measures, so an unknown goal
    /// id is logged and ignored instead of throwing.
    /// </summary>
    [Fact]
    public async Task AddGoalUsageAsync_UnknownGoalId_LogsAWarningAndDoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new RecordingLogger<GoalStore>();
        var store = new GoalStore(_dbContext, logger);

        await store.AddGoalUsageAsync("no-such-goal", GoalUsageKind.Brain,
            Summary(Entry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = 5, Calls = 1 })), ct);

        // Nothing was created, and the miss is visible in the log.
        Assert.Null(await _store.GetGoalAsync("no-such-goal", ct));
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("no-such-goal", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Brain", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A DATABASE failure must also be swallowed: the recording is best-effort, so a store whose
    /// context cannot even be created completes the call instead of surfacing the failure into the
    /// Brain reply or the review verdict.
    /// </summary>
    [Fact]
    public async Task AddGoalUsageAsync_PersistenceFailure_IsSwallowedAndLogged()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new RecordingLogger<GoalStore>();
        var store = new GoalStore(new ThrowingContextFactory(), logger);

        await store.AddGoalUsageAsync("usage-goal", GoalUsageKind.PreExecutionReview,
            Summary(Entry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = 5, Calls = 1 })), ct);

        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("PreExecutionReview", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The interface declaration is a DEFAULT implementation, so stores and test doubles that do not
    /// persist usage (the shared <see cref="InMemoryGoalStore"/> among them — the whole test project
    /// compiles with it unchanged) still satisfy <see cref="IGoalStore"/> and a call through the
    /// interface completes as a no-op instead of throwing.
    /// </summary>
    [Fact]
    public async Task AddGoalUsageAsync_DefaultInterfaceImplementation_IsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;

        var method = typeof(IGoalStore).GetMethod(nameof(IGoalStore.AddGoalUsageAsync));
        Assert.NotNull(method);
        Assert.False(method!.IsAbstract);
        Assert.NotNull(method.GetMethodBody());

        // A fake that does NOT override the method: the call must complete silently.
        var fake = new InMemoryGoalStore();
        fake.AddGoal(new Goal { Id = "fake-goal", Description = "Fake store goal" });
        IGoalStore asInterface = fake;

        await asInterface.AddGoalUsageAsync("fake-goal", GoalUsageKind.Brain,
            Summary(Entry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = 5, Calls = 1 })), ct);

        var goal = await asInterface.GetGoalAsync("fake-goal", ct);
        Assert.Null(goal!.BrainUsage);
        Assert.Null(goal.ReviewUsage);
    }

    // ── Stale instances must not overwrite the accumulated values ─────────

    /// <summary>
    /// <see cref="GoalStore.UpdateGoalAsync"/> copies the mutable fields of the passed-in instance
    /// onto the stored row, but it must NOT copy the two usage properties: they are written only by
    /// <see cref="IGoalStore.AddGoalUsageAsync"/>. A stale instance — for example
    /// <c>pipeline.Goal</c>, loaded long before the Brain calls completed — would otherwise erase
    /// usage that has already been recorded.
    /// </summary>
    [Fact]
    public async Task UpdateGoalAsync_StaleInstance_CannotOverwriteStoredUsage()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal(), ct);
        var storedBrainInput = await SeedUsageAsync(ct);

        // (a) A stale instance that knows nothing about usage (both properties null).
        var stale = await _store.GetGoalAsync("usage-goal", ct);
        Assert.NotNull(stale);
        stale!.Description = "Updated by a stale instance";
        stale.BrainUsage = null;
        stale.ReviewUsage = null;
        await _store.UpdateGoalAsync(stale, ct);

        var afterNullStale = await _store.GetGoalAsync("usage-goal", ct);
        Assert.Equal("Updated by a stale instance", afterNullStale!.Description);
        Assert.Equal(storedBrainInput, Bucket(afterNullStale.BrainUsage, UsageSource.Agent, "brain-model").Usage.InputTokens);
        Assert.Equal(222, Bucket(afterNullStale.ReviewUsage, UsageSource.Agent, "review-model").Usage.InputTokens);

        // (b) A stale instance carrying DIFFERENT non-null values — still not copied.
        var staleWithValues = await _store.GetGoalAsync("usage-goal", ct);
        Assert.NotNull(staleWithValues);
        staleWithValues!.Description = "Updated by a stale instance with values";
        staleWithValues.BrainUsage = Summary(Entry(UsageSource.Agent, "stale-model", new TokenUsage { InputTokens = 999_999, Calls = 99 }));
        staleWithValues.ReviewUsage = Summary(Entry(UsageSource.Agent, "stale-model", new TokenUsage { InputTokens = 888_888, Calls = 88 }));
        await _store.UpdateGoalAsync(staleWithValues, ct);

        var afterValueStale = await _store.GetGoalAsync("usage-goal", ct);
        Assert.Equal("Updated by a stale instance with values", afterValueStale!.Description);
        Assert.Single(afterValueStale.BrainUsage!.Entries);
        Assert.Equal(storedBrainInput, Bucket(afterValueStale.BrainUsage, UsageSource.Agent, "brain-model").Usage.InputTokens);
        Assert.Single(afterValueStale.ReviewUsage!.Entries);
        Assert.Equal(222, Bucket(afterValueStale.ReviewUsage, UsageSource.Agent, "review-model").Usage.InputTokens);
    }

    /// <summary>
    /// The same guarantee for the status path, whose metadata record carries no usage field at all:
    /// a status update (with and without metadata) leaves both accumulated values untouched.
    /// </summary>
    [Fact]
    public async Task UpdateGoalStatusAsync_LeavesStoredUsageUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal(), ct);
        var storedBrainInput = await SeedUsageAsync(ct);

        await _store.UpdateGoalStatusAsync("usage-goal", GoalStatus.InProgress, null, ct);
        await _store.UpdateGoalStatusAsync("usage-goal", GoalStatus.Completed, new GoalUpdateMetadata
        {
            CompletedAt = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc),
            Iterations = 2,
            Notes = ["a note"],
        }, ct);

        var goal = await _store.GetGoalAsync("usage-goal", ct);
        Assert.NotNull(goal);
        Assert.Equal(GoalStatus.Completed, goal!.Status);
        Assert.Equal(storedBrainInput, Bucket(goal.BrainUsage, UsageSource.Agent, "brain-model").Usage.InputTokens);
        Assert.Equal(222, Bucket(goal.ReviewUsage, UsageSource.Agent, "review-model").Usage.InputTokens);
    }

    // ── Reset: Brain usage is part of the run, reviews are not ────────────

    /// <summary>
    /// The Failed → Draft reset discards the run, so the run's Brain usage goes with it — like the
    /// iteration summaries. <see cref="Goal.ReviewUsage"/> stays: a pre-execution review happens
    /// before the run and the reviews already paid for must survive a retry.
    /// </summary>
    [Fact]
    public async Task ResetGoalIterationDataAsync_ClearsBrainUsageAndKeepsReviewUsage()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal(), ct);
        await SeedUsageAsync(ct);

        await _store.ResetGoalIterationDataAsync("usage-goal", ct);

        var goal = await _store.GetGoalAsync("usage-goal", ct);
        Assert.NotNull(goal);
        Assert.Null(goal!.BrainUsage);
        Assert.Equal(222, Bucket(goal.ReviewUsage, UsageSource.Agent, "review-model").Usage.InputTokens);
        Assert.Equal(1, Bucket(goal.ReviewUsage, UsageSource.Agent, "review-model").Usage.Calls);
    }

    // ── Concurrency ───────────────────────────────────────────────────────

    /// <summary>
    /// THE DETERMINISTIC SERIALIZATION PROOF for <see cref="IGoalStore.AddGoalUsageAsync"/> — it does
    /// not depend on thread-pool scheduling at all.
    /// <para>
    /// The FIRST add is held INSIDE the production method, at the save boundary (after its tracked-row
    /// load and merge, before the UPDATE is issued), by a checkpointing EF interceptor the test owns;
    /// while it is held it also holds the store's gate, because the gate is taken before any row work.
    /// The SECOND add is then invoked <b>directly on the test thread</b>, so it runs synchronously to
    /// its first real suspension point:
    /// </para>
    /// <list type="bullet">
    ///   <item>with the gate, that first suspension is the contended <c>SemaphoreSlim.WaitAsync</c>,
    ///   which happens BEFORE <c>ResolveDbContext()</c> — so no second context exists and the
    ///   synchronous assertion below passes;</item>
    ///   <item>without the gate, <c>ResolveDbContext()</c> runs synchronously on the same thread and
    ///   the second context is created before the call returns — so the same assertion fails, on every
    ///   schedule, because there is no schedule: the failing work happens on the test's own thread
    ///   before the assertion executes.</item>
    /// </list>
    /// <para>
    /// The observation is a production-caused side effect (context creation through the wrapper
    /// factory), not a counter that can lag: the store cannot reach its merge or its save without
    /// having created a context first. The hold is released in <c>finally</c>; both operations are
    /// joined and the FRESH read-back must show both adds merged.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AddGoalUsageAsync_SecondAddWaitsForTheFirst_SaveCheckpointProvesSerialization()
    {
        var ct = TestContext.Current.CancellationToken;
        using var rig = new SerializationRig();
        rig.SeedGoal();
        var store = rig.CreateStore();

        // The hold the test owns: the first add's save parks here, holding the store's gate.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.HoldFirstSave(hold.Task);

        Task? first = null;
        Task? second = null;
        try
        {
            // (1) The FIRST add: loads + merges, then parks inside its save.
            first = Task.Run(
                () => store.AddGoalUsageAsync("serial-usage-goal", GoalUsageKind.Brain,
                    Summary(Entry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = 100, Calls = 1 })),
                    CancellationToken.None),
                ct);

            await rig.SaveEntered.WaitAsync(HangGuard, ct);
            Assert.Equal(1, rig.ContextsCreated);

            // (2) The SECOND add, invoked on THIS thread while the first is held: it runs
            // synchronously up to its first real suspension.
            second = store.AddGoalUsageAsync("serial-usage-goal", GoalUsageKind.Brain,
                Summary(Entry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = 7, Calls = 1 })),
                CancellationToken.None);

            // (3) SYNCHRONOUS production-caused observation: no second context was created, so the
            // second add is parked on the store's gate and never entered the load/merge/save section.
            Assert.False(rig.SecondContextCreated.Task.IsCompleted,
                "The second add created its context while the first add was still inside the store's "
                + "critical section: concurrent adds are NOT serialised, so an update can be lost.");
            Assert.Equal(1, rig.ContextsCreated);

            // (4) Release, join in a deterministic pair, then read the FRESH totals back.
            hold.SetResult();
            await Task.WhenAll(first, second).WaitAsync(HangGuard, ct);

            var readBack = await store.GetGoalAsync("serial-usage-goal", ct);
            var merged = Bucket(readBack!.BrainUsage, UsageSource.Agent, "model-a").Usage;
            Assert.Equal(107, merged.InputTokens);
            Assert.Equal(2, merged.Calls);
        }
        finally
        {
            // Never leave a held add parked, even when an assertion above failed.
            hold.TrySetResult();
            await rig.JoinQuietlyAsync(first, second, ct);
        }
    }

    /// <summary>
    /// A scheduling-based smoke case kept ALONGSIDE the deterministic proof above: eight adds of the
    /// same kind are started together, each with a distinct token count, and all must be reflected in
    /// the merged totals. This catches a lost update on an ordinary schedule; the checkpoint test above
    /// is what makes the gate's presence provable without relying on one.
    /// </summary>
    [Fact]
    public async Task AddGoalUsageAsync_ConcurrentAdds_PreserveEveryUpdate()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = $"Data Source=file:memdb-goalusage-{Guid.NewGuid():N}?mode=memory&cache=shared";

        using var keeper = new SqliteConnection(connectionString);
        keeper.Open();

        var seedOptions = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connectionString).Options;
        using (var seedContext = new CopilotHiveDbContext(seedOptions))
        {
            seedContext.Database.EnsureCreated();
            seedContext.Goals.Add(MakeGoal("concurrent-usage-goal"));
            await seedContext.SaveChangesAsync(ct);
        }

        using var factory = new SharedCacheContextFactory(connectionString);
        var store = new GoalStore(factory, NullLogger<GoalStore>.Instance);

        const int writers = 8;
        var adds = new List<Task>(writers);
        for (var i = 1; i <= writers; i++)
        {
            var input = i * 10;
            adds.Add(Task.Run(() => store.AddGoalUsageAsync("concurrent-usage-goal", GoalUsageKind.Brain,
                Summary(Entry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = input, Calls = 1 })),
                CancellationToken.None), ct));
        }

        await Task.WhenAll(adds).WaitAsync(HangGuard, ct);

        var goal = await store.GetGoalAsync("concurrent-usage-goal", ct);
        var merged = Bucket(goal!.BrainUsage, UsageSource.Agent, "model-a").Usage;
        Assert.Equal(10 * (writers * (writers + 1) / 2), merged.InputTokens);
        Assert.Equal(writers, merged.Calls);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Records one Brain add (111 input tokens) and one review add (222 input tokens, 1 call) on
    /// <c>usage-goal</c> through the production entry point and returns the stored Brain input total.
    /// </summary>
    private async Task<long> SeedUsageAsync(CancellationToken ct)
    {
        await _store.AddGoalUsageAsync("usage-goal", GoalUsageKind.Brain,
            Summary(Entry(UsageSource.Agent, "brain-model", new TokenUsage { InputTokens = 111, Calls = 1 })), ct);
        await _store.AddGoalUsageAsync("usage-goal", GoalUsageKind.PreExecutionReview,
            Summary(Entry(UsageSource.Agent, "review-model", new TokenUsage { InputTokens = 222, Calls = 1 })), ct);

        var goal = await _store.GetGoalAsync("usage-goal", ct);
        return Bucket(goal!.BrainUsage, UsageSource.Agent, "brain-model").Usage.InputTokens;
    }

    /// <summary>
    /// An <see cref="IDbContextFactory{TContext}"/> over a shared-cache in-memory SQLite database, so
    /// every <see cref="IGoalStore.AddGoalUsageAsync"/> call gets its own context and connection while
    /// all of them see the same database — the production shape.
    /// </summary>
    private sealed class SharedCacheContextFactory(string connectionString) : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        public CopilotHiveDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connectionString).Options);

        public void Dispose() { }
    }

    /// <summary>
    /// The deterministic serialization harness: a real <see cref="GoalStore"/> over a shared-cache
    /// in-memory SQLite database, whose context factory COUNTS every context the store creates
    /// (the production-caused side effect the serialization test observes) and whose
    /// <see cref="HoldFirstSaveInterceptor"/> parks the FIRST save at the save boundary so the test can
    /// inspect the store's critical section while one add is inside it.
    /// <para>
    /// Everything here is test-only: the store, its gate and its save path are the production ones,
    /// reached through <see cref="IDbContextFactory{TContext}"/>, which is the store's own injection
    /// seam. No production file is touched by this rig.
    /// </para>
    /// </summary>
    private sealed class SerializationRig : IDisposable
    {
        private readonly string _connectionString;
        private readonly SqliteConnection _keeper;
        private readonly HoldFirstSaveInterceptor _interceptor = new();
        private readonly List<CopilotHiveDbContext> _contexts = [];
        private int _contextsCreated;

        internal SerializationRig()
        {
            // A shared-cache in-memory database anchored by a keeper connection: every context the
            // store creates sees the same data, exactly like the file-backed production database.
            _connectionString = $"Data Source=file:memdb-goalusage-serial-{Guid.NewGuid():N}?mode=memory&cache=shared";
            _keeper = new SqliteConnection(_connectionString);
            _keeper.Open();
        }

        /// <summary>How many contexts the store has created so far — a production-caused side effect.</summary>
        internal int ContextsCreated => Volatile.Read(ref _contextsCreated);

        /// <summary>Resolved when a SECOND context is created (i.e. a second add entered the store).</summary>
        internal TaskCompletionSource SecondContextCreated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Resolved when the held save is entered (the first add is inside the store).</summary>
        internal Task SaveEntered => _interceptor.SaveEntered;

        /// <summary>Arms the interceptor so the FIRST save parks until <paramref name="hold"/> completes.</summary>
        internal void HoldFirstSave(Task hold) => _interceptor.Hold(hold);

        internal void SeedGoal()
        {
            using var context = new CopilotHiveDbContext(
                new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(_connectionString).Options);
            context.Database.EnsureCreated();
            context.Goals.Add(MakeGoal("serial-usage-goal"));
            context.SaveChanges();
        }

        internal GoalStore CreateStore() => new(new RigContextFactory(this), NullLogger<GoalStore>.Instance);

        internal CopilotHiveDbContext CreateContext()
        {
            var context = new CopilotHiveDbContext(
                new DbContextOptionsBuilder<CopilotHiveDbContext>()
                    .UseSqlite(_connectionString)
                    .AddInterceptors(_interceptor)
                    .Options);
            _contexts.Add(context);

            // Creation COMPLETED: the side effect the serialization assertion reads.
            if (Interlocked.Increment(ref _contextsCreated) >= 2)
                SecondContextCreated.TrySetResult();

            return context;
        }

        /// <summary>
        /// Joins the in-flight adds without ever masking the test's own assertion: teardown is
        /// best-effort, and the bounded await is a HANG GUARD only (the operations complete because the
        /// hold was released, never because time passed).
        /// </summary>
        internal async Task JoinQuietlyAsync(Task? first, Task? second, CancellationToken ct)
        {
            var pending = new List<Task>(2);
            if (first is not null) pending.Add(first);
            if (second is not null) pending.Add(second);
            if (pending.Count == 0)
                return;

            try
            {
                await Task.WhenAll(pending).WaitAsync(HangGuard, ct);
            }
            catch (Exception)
            {
                // The test's own assertions already reported; draining is cleanup only.
            }
        }

        public void Dispose()
        {
            foreach (var context in _contexts)
            {
                try
                {
                    context.Dispose();
                }
                catch (Exception)
                {
                    // Best-effort teardown.
                }
            }

            _keeper.Dispose();
        }

        private sealed class RigContextFactory(SerializationRig rig) : IDbContextFactory<CopilotHiveDbContext>
        {
            public CopilotHiveDbContext CreateDbContext() => rig.CreateContext();
        }
    }

    /// <summary>
    /// Parks the FIRST <c>SaveChangesAsync</c> it sees at the SAVE BOUNDARY — after the tracked row was
    /// loaded and merged, before any UPDATE is issued — until the test releases the hold. Every later
    /// save passes straight through, so only the add the test wants to hold is affected.
    /// </summary>
    private sealed class HoldFirstSaveInterceptor : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _hold;
        private int _claimed;

        internal Task SaveEntered => _entered.Task;

        internal void Hold(Task hold) => _hold = hold;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _claimed, 1) == 0)
            {
                // Announce entry BEFORE parking, so the test can observe the held critical section.
                _entered.TrySetResult();

                if (_hold is { } hold)
                    await hold.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    /// <summary>
    /// An <see cref="IDbContextFactory{TContext}"/> whose context creation always fails, so the
    /// best-effort error path of <see cref="IGoalStore.AddGoalUsageAsync"/> is exercised.
    /// </summary>
    private sealed class ThrowingContextFactory : IDbContextFactory<CopilotHiveDbContext>
    {
        public CopilotHiveDbContext CreateDbContext() =>
            throw new InvalidOperationException("no database configured for this test");

        public Task<CopilotHiveDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("no database configured for this test");
    }

    /// <summary>Captures the messages a store logs, so the unknown-goal warning is assertable.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}

/// <summary>
/// The SCHEMA half of the per-goal usage contract: the <c>brain_usage</c> / <c>review_usage</c>
/// columns are part of the model (so a fresh database has them), they are added to an existing
/// <c>goals</c> table that predates them by
/// <see cref="DatabaseMigration.EnsureSchemaUpToDate"/> → <c>ReconcileColumns</c> (no EF migration
/// file), legacy rows read back with both properties <c>null</c>, and each kind is written to its OWN
/// column.
/// </summary>
public sealed class GoalUsageSchemaTests
{
    private static readonly string[] UsageColumns = ["brain_usage", "review_usage"];

    /// <summary>The column list of an existing <c>goals</c> table created before the usage columns.</summary>
    private const string LegacyGoalsTable =
        """
        CREATE TABLE goals (
            id TEXT NOT NULL PRIMARY KEY,
            description TEXT NOT NULL,
            status TEXT NOT NULL DEFAULT 'pending',
            priority TEXT NOT NULL DEFAULT 'normal',
            scope TEXT NOT NULL DEFAULT 'patch',
            review_status TEXT NOT NULL DEFAULT 'none',
            repositories TEXT,
            metadata TEXT,
            created_at TEXT NOT NULL,
            started_at TEXT,
            completed_at TEXT,
            iterations INTEGER NOT NULL DEFAULT 0,
            failure_reason TEXT,
            notes TEXT,
            phase_durations TEXT,
            total_duration_seconds REAL,
            depends_on TEXT,
            documents TEXT,
            branch_cleaned_up INTEGER NOT NULL DEFAULT 0,
            merge_commit_hash TEXT,
            release_id TEXT,
            title TEXT,
            source_conversation_id TEXT
        )
        """;

    /// <summary>Creates a DbContext over an open in-memory connection WITHOUT creating any schema.</summary>
    private static CopilotHiveDbContext CreateEmptyDbContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        return new CopilotHiveDbContext(
            new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection).Options);
    }

    private static SqliteConnection GetSqliteConnection(CopilotHiveDbContext ctx) =>
        (SqliteConnection)ctx.Database.GetDbConnection();

    private static HashSet<string> GetTableColumns(SqliteConnection conn, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }

    /// <summary>
    /// Executes a SQL statement directly on the connection, bypassing <c>ExecuteSqlRaw</c>'s
    /// format-placeholder parsing (which misreads literal <c>'{}'</c> braces in a JSON literal).
    /// </summary>
    private static void ExecuteDirect(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// A fresh database has BOTH usage columns, and they are nullable (no default), so a newly
    /// inserted goal row leaves them SQL NULL.
    /// </summary>
    [Fact]
    public void FreshDatabase_GoalsTableHasBothNullableUsageColumns()
    {
        using var ctx = CopilotHiveDbContext.CreateInMemory();
        var conn = GetSqliteConnection(ctx);

        var columns = GetTableColumns(conn, "goals");
        Assert.Contains("brain_usage", columns);
        Assert.Contains("review_usage", columns);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('goals') WHERE name IN ('brain_usage', 'review_usage') AND \"notnull\" = 0 AND dflt_value IS NULL";
        Assert.Equal(2L, cmd.ExecuteScalar());
    }

    /// <summary>
    /// An EXISTING database whose <c>goals</c> table predates the columns gains both through
    /// <see cref="DatabaseMigration.EnsureSchemaUpToDate"/>: the columns are added as nullable with no
    /// default, and a pre-existing (legacy) row reads back with both usage properties <c>null</c> —
    /// "nothing recorded", not an empty summary.
    /// </summary>
    [Fact]
    public async Task EnsureSchemaUpToDate_ExistingGoalsTableWithoutUsageColumns_AddsThemAndLegacyRowReadsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = CreateEmptyDbContext();
        var conn = GetSqliteConnection(ctx);

        ctx.Database.ExecuteSqlRaw(LegacyGoalsTable);
        // The timestamp uses the store's canonical round-trip ("O") text form and the legacy row
        // carries the JSON collections the model reads as non-nullable, so the row is readable through
        // the domain model as well as through raw SQL. Executed through a raw DbCommand (NOT
        // ExecuteSqlRaw) so the literal '{}' braces are not read as format placeholders.
        ExecuteDirect(conn,
            """
            INSERT INTO goals (id, description, repositories, metadata, notes, depends_on, documents,
                               created_at, branch_cleaned_up)
            VALUES ('legacy-usage-goal', 'Legacy goal', '[]', '{}', '[]', '[]', '[]',
                    '2025-01-01T00:00:00.0000000Z', 0)
            """);

        // Sanity: the table really is missing both columns before the reconciliation.
        var before = GetTableColumns(conn, "goals");
        Assert.DoesNotContain("brain_usage", before);
        Assert.DoesNotContain("review_usage", before);

        DatabaseMigration.EnsureSchemaUpToDate(ctx, NullLogger.Instance);

        var after = GetTableColumns(conn, "goals");
        Assert.Contains("brain_usage", after);
        Assert.Contains("review_usage", after);

        // The legacy row's added columns are SQL NULL…
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT brain_usage, review_usage FROM goals WHERE id = 'legacy-usage-goal'";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
        }

        // …and reading the row through the domain model yields null for both (not an empty summary).
        var store = new GoalStore(ctx, NullLogger<GoalStore>.Instance);
        var goal = await store.GetGoalAsync("legacy-usage-goal", ct);
        Assert.NotNull(goal);
        Assert.Null(goal!.BrainUsage);
        Assert.Null(goal.ReviewUsage);

        // The same database is now writable through the production entry point.
        await store.AddGoalUsageAsync("legacy-usage-goal", GoalUsageKind.Brain,
            new UsageSummary([new UsageEntry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = 7, Calls = 1 })]), ct);
        var afterAdd = await store.GetGoalAsync("legacy-usage-goal", ct);
        Assert.Equal(7, Assert.Single(afterAdd!.BrainUsage!.Entries).Usage.InputTokens);
    }

    /// <summary>
    /// Each kind lands in its OWN column: the Brain add's model appears only in <c>brain_usage</c> and
    /// the review add's model only in <c>review_usage</c>, read straight from the raw JSON text.
    /// </summary>
    [Fact]
    public async Task AddGoalUsageAsync_WritesEachKindToItsOwnJsonColumn()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = CopilotHiveDbContext.CreateInMemory();
        var conn = GetSqliteConnection(ctx);
        var store = new GoalStore(ctx, NullLogger<GoalStore>.Instance);

        await store.CreateGoalAsync(new Goal { Id = "column-goal", Description = "Column mapping" }, ct);
        await store.AddGoalUsageAsync("column-goal", GoalUsageKind.Brain,
            new UsageSummary([new UsageEntry(UsageSource.Agent, "brain-only-model", new TokenUsage { InputTokens = 11, Calls = 1 })]), ct);
        await store.AddGoalUsageAsync("column-goal", GoalUsageKind.PreExecutionReview,
            new UsageSummary([new UsageEntry(UsageSource.Agent, "review-only-model", new TokenUsage { InputTokens = 22, Calls = 1 })]), ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT brain_usage, review_usage FROM goals WHERE id = 'column-goal'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        var brainJson = reader.GetString(0);
        var reviewJson = reader.GetString(1);
        Assert.Contains("brain-only-model", brainJson, StringComparison.Ordinal);
        Assert.DoesNotContain("review-only-model", brainJson, StringComparison.Ordinal);
        Assert.Contains("review-only-model", reviewJson, StringComparison.Ordinal);
        Assert.DoesNotContain("brain-only-model", reviewJson, StringComparison.Ordinal);

        // The columns are the ones the design names.
        Assert.All(UsageColumns, column => Assert.Contains(column, GetTableColumns(conn, "goals")));
    }
}
