using System.Data.Common;

using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// THE BORROWED-CONTEXT CONVERSATION-SCOPE GUARD of BOTH full-save routes: once conversation
/// replacement starts, that operation OWNS the target goal's conversation tracking scope, so a
/// failed full save must not leave the rejected conversation replacement staged on a
/// caller-supplied context — where a LATER state-only save on the SAME borrowed context would
/// flush it.
/// </summary>
/// <remarks>
/// <para>
/// Every vector runs against a REAL shared-cache SQLite database whose durable rows are read back
/// RAW through a keeper connection (no EF, no change tracker in the way), and every fault is a
/// genuine <see cref="DbCommandInterceptor"/> throw or a genuine staging fault inside
/// <c>SaveConversationCore</c> — never a fabricated token.
/// </para>
/// <para>
/// THE MUTATION THE MATRIX KILLS: dropping the conversation-scope cleanup (or gating it wrongly)
/// leaves the rejected replacement/removal tracked, so the following state-only save flushes it and
/// the durable conversation equality assertions fail.
/// </para>
/// </remarks>
public sealed class PipelineStoreConversationFailureTests : IDisposable
{
    private readonly string _connectionString =
        $"Data Source=file:memdb-fullsave-conv-{Guid.NewGuid():N}?mode=memory&cache=shared";

    private readonly SqliteConnection _keeper;
    private readonly List<DbConnection> _connections = [];
    private readonly List<CopilotHiveDbContext> _contexts = [];
    private readonly List<TestFactory> _factories = [];

    public PipelineStoreConversationFailureTests()
    {
        _keeper = new SqliteConnection(_connectionString);
        _keeper.Open();
        CreateContext().Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
            factory.Dispose();
        foreach (var context in _contexts)
            context.Dispose();
        foreach (var connection in _connections)
            connection.Dispose();
        _keeper.Dispose();
    }

    // ═════════════════════════════════════ fixture plumbing ═════════════════════════════════════

    private CopilotHiveDbContext CreateContext(IInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        _connections.Add(connection);

        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);

        var context = new CopilotHiveDbContext(builder.Options);
        _contexts.Add(context);
        return context;
    }

    private PipelineStore CreateStore(IInterceptor? interceptor = null, ILogger<PipelineStore>? logger = null) =>
        new(CreateContext(interceptor), logger ?? NullLogger<PipelineStore>.Instance);

    private static Goal NewGoal(string id) =>
        new() { Id = id, Description = "goal " + id, RepositoryNames = ["test-repo"] };

    private static GoalPipeline NewPipeline(string goalId) => new(NewGoal(goalId));

    /// <summary>Seeds the durable pipeline row AND its conversation through a SEPARATE context.</summary>
    private void SeedPipelineWithConversation(string goalId, params (string Role, string Content)[] entries)
    {
        var seeding = CreateStore();
        var pipeline = NewPipeline(goalId);
        foreach (var (role, content) in entries)
            pipeline.Conversation.Add(new ConversationEntry(role, content));
        seeding.SavePipeline(pipeline);
    }

    /// <summary>The raw durable conversation rows, ordered by <c>seq</c> — id, order, role and text.</summary>
    private List<(long Id, int Seq, string Role, string Content)> ReadConversation(string goalId)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText =
            "SELECT id, seq, role, content FROM conversation_entries WHERE goal_id = $goal ORDER BY seq, id";
        command.Parameters.AddWithValue("$goal", goalId);
        using var reader = command.ExecuteReader();

        var rows = new List<(long Id, int Seq, string Role, string Content)>();
        while (reader.Read())
            rows.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
        return rows;
    }

    private string? RawScalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result as string;
    }

    /// <summary>Identical rows — identity, order, role and content — before and after.</summary>
    private static void AssertConversationUnchanged(
        List<(long Id, int Seq, string Role, string Content)> expected,
        List<(long Id, int Seq, string Role, string Content)> actual) =>
        Assert.Equal(expected, actual);

    private static IEnumerable<Exception> EnumerateChain(Exception exception)
    {
        for (var current = (Exception?)exception; current is not null; current = current.InnerException)
            yield return current;
    }

    /// <summary>Runs the full save on the requested route — the legacy two-argument form or the
    /// ownership-aware form — so every vector covers BOTH.</summary>
    private static void FullSave(PipelineStore store, GoalPipeline pipeline, bool ownershipRoute)
    {
        if (ownershipRoute)
            store.SavePipeline(pipeline, pipeline.CaptureAdmissionOwnership());
        else
            store.SavePipeline(pipeline);
    }

    private bool HasTrackedConversation(CopilotHiveDbContext context, string goalId) =>
        context.ChangeTracker.Entries<ConversationEntryEntity>()
            .Any(e => string.Equals(e.Entity.GoalId, goalId, StringComparison.Ordinal));

    // ═══════════════════════════════════ (1) the three shapes ═══════════════════════════════════

    /// <summary>
    /// REPLACEMENT: the durable conversation exists, the full save deletes it and stages the
    /// replacement, the flush fails before the commit — and the caller's NEXT state-only save on the
    /// SAME borrowed context, with no manual tracker clearing, cannot flush the rejected replacement.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullSave_BorrowedContext_RejectedConversationReplacement_StateSaveCannotFlushIt(bool ownershipRoute)
    {
        const string goalId = "conv-replace-goal";
        var sentinel = new InvalidOperationException("conversation-replace-sentinel");

        SeedPipelineWithConversation(goalId, ("user", "original-1"), ("assistant", "original-2"));
        var durable = ReadConversation(goalId);
        Assert.Equal(2, durable.Count);

        var interceptor = new OneShotPipelinesDmlThrowInterceptor(sentinel);
        var store = CreateStore(interceptor);

        var pipeline = NewPipeline(goalId);
        pipeline.Conversation.Add(new ConversationEntry("user", "replacement-1"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "replacement-2"));
        pipeline.AdvanceTo(GoalPhase.Coding);

        var thrown = Record.Exception(() => FullSave(store, pipeline, ownershipRoute));

        Assert.NotNull(thrown);
        Assert.Contains(sentinel, EnumerateChain(thrown!));
        Assert.Equal(1, interceptor.ThrowCount);   // the injection really fired, once
        AssertConversationUnchanged(durable, ReadConversation(goalId));

        // THE LEAK: a LATER state-only save on the SAME borrowed context, WITHOUT manually clearing
        // tracking. Only the scalar progress may land.
        pipeline.AdvanceTo(GoalPhase.Testing);
        store.SavePipelineState(pipeline);

        Assert.Equal("Testing", RawScalar(
            "SELECT phase FROM pipelines WHERE goal_id = $goal", ("$goal", goalId)));
        AssertConversationUnchanged(durable, ReadConversation(goalId));
    }

    /// <summary>
    /// DELETION-ONLY: the replacement conversation is EMPTY, so the rejected mutation is a pure set of
    /// tracked deletes — the later state-only save must not flush those either.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullSave_BorrowedContext_RejectedConversationDeletionOnly_StateSaveCannotFlushIt(bool ownershipRoute)
    {
        const string goalId = "conv-delete-only-goal";
        var sentinel = new InvalidOperationException("conversation-delete-only-sentinel");

        SeedPipelineWithConversation(goalId, ("user", "original-1"), ("assistant", "original-2"));
        var durable = ReadConversation(goalId);
        Assert.Equal(2, durable.Count);

        var interceptor = new OneShotPipelinesDmlThrowInterceptor(sentinel);
        var store = CreateStore(interceptor);

        // The replacement is EMPTY: only the removal of the durable entries is staged.
        var pipeline = NewPipeline(goalId);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var thrown = Record.Exception(() => FullSave(store, pipeline, ownershipRoute));

        Assert.NotNull(thrown);
        Assert.Contains(sentinel, EnumerateChain(thrown!));
        Assert.Equal(1, interceptor.ThrowCount);
        AssertConversationUnchanged(durable, ReadConversation(goalId));

        pipeline.AdvanceTo(GoalPhase.Testing);
        store.SavePipelineState(pipeline);

        Assert.Equal("Testing", RawScalar(
            "SELECT phase FROM pipelines WHERE goal_id = $goal", ("$goal", goalId)));
        AssertConversationUnchanged(durable, ReadConversation(goalId));
    }

    /// <summary>
    /// ADDITION-ONLY: the durable conversation is EMPTY, so the rejected mutation is a pure set of
    /// tracked additions — the later state-only save must not flush those either.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullSave_BorrowedContext_RejectedConversationAdditionOnly_StateSaveCannotFlushIt(bool ownershipRoute)
    {
        const string goalId = "conv-add-only-goal";
        var sentinel = new InvalidOperationException("conversation-add-only-sentinel");

        SeedPipelineWithConversation(goalId);
        var durable = ReadConversation(goalId);
        Assert.Empty(durable);

        var interceptor = new OneShotPipelinesDmlThrowInterceptor(sentinel);
        var store = CreateStore(interceptor);

        var pipeline = NewPipeline(goalId);
        pipeline.Conversation.Add(new ConversationEntry("user", "addition-1"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "addition-2"));
        pipeline.AdvanceTo(GoalPhase.Coding);

        var thrown = Record.Exception(() => FullSave(store, pipeline, ownershipRoute));

        Assert.NotNull(thrown);
        Assert.Contains(sentinel, EnumerateChain(thrown!));
        Assert.Equal(1, interceptor.ThrowCount);
        Assert.Empty(ReadConversation(goalId));

        pipeline.AdvanceTo(GoalPhase.Testing);
        store.SavePipelineState(pipeline);

        Assert.Equal("Testing", RawScalar(
            "SELECT phase FROM pipelines WHERE goal_id = $goal", ("$goal", goalId)));
        // The rejected additions never landed.
        Assert.Empty(ReadConversation(goalId));
    }

    // ═══════════════════ (2) a throw from INSIDE SaveConversationCore ════════════════════════════

    /// <summary>
    /// PARTIAL STAGING: the staging itself throws AFTER the deletes and AFTER at least one addition —
    /// a deterministic fault from inside <c>SaveConversationCore</c>, so the cleanup must be armed
    /// BEFORE that call, not after it. Neither the rejected deletes nor the partially staged addition
    /// may be flushed by the following state-only save.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: setting the "replacement was attempted" flag AFTER
    /// <c>SaveConversationCore</c> returns (the natural-looking placement) leaves this vector's
    /// partially staged conversation tracked — the follow-up state save then flushes it and both
    /// equality assertions fail.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullSave_BorrowedContext_PartialStagingThrow_RejectedMutationsCannotBeFlushedLater(bool ownershipRoute)
    {
        const string goalId = "conv-partial-staging-goal";

        SeedPipelineWithConversation(goalId, ("user", "original-1"), ("assistant", "original-2"));
        var durable = ReadConversation(goalId);
        Assert.Equal(2, durable.Count);

        // NO interceptor: the fault is the staging work itself.
        var store = CreateStore();

        var pipeline = NewPipeline(goalId);
        pipeline.Conversation.Add(new ConversationEntry("user", "partial-1"));  // staged BEFORE the fault
        pipeline.Conversation.Add(null!);                                       // the staging fault
        pipeline.Conversation.Add(new ConversationEntry("assistant", "partial-3"));
        pipeline.AdvanceTo(GoalPhase.Coding);

        var thrown = Record.Exception(() => FullSave(store, pipeline, ownershipRoute));

        Assert.IsType<NullReferenceException>(thrown);
        // The deletes WERE staged before the fault: the durable rows are still there (nothing was
        // flushed) but the tracker holds them in the Deleted state.
        AssertConversationUnchanged(durable, ReadConversation(goalId));

        pipeline.AdvanceTo(GoalPhase.Testing);
        store.SavePipelineState(pipeline);

        Assert.Equal("Testing", RawScalar(
            "SELECT phase FROM pipelines WHERE goal_id = $goal", ("$goal", goalId)));
        // Neither the rejected deletes nor the partially staged addition landed.
        AssertConversationUnchanged(durable, ReadConversation(goalId));
    }

    // ═════════════ (3) a failure BEFORE conversation replacement starts ══════════════════════════

    /// <summary>
    /// THE ATTEMPT GATE, from the other side: a full-save failure that happens BEFORE conversation
    /// replacement starts must NOT discard the CALLER's own conversation entries — they are still
    /// tracked, still pending, and still flushed by the caller's next save.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: running the conversation-scope cleanup unconditionally in the catch
    /// (no attempt flag) detaches the caller's pre-existing pending edit, so both the tracking probe
    /// and the flush probe fail.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullSave_BorrowedContext_FailureBeforeConversationReplacement_KeepsCallerConversationEntries(bool ownershipRoute)
    {
        const string goalId = "conv-prestage-goal";
        var sentinel = new InvalidOperationException("pre-staging-select-sentinel");

        SeedPipelineWithConversation(goalId);

        var context = CreateContext(new OneShotPipelinesSelectThrowInterceptor(sentinel));
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        // THE CALLER'S PENDING EDIT, staged on the borrowed context BEFORE the full save.
        var callerEntry = new ConversationEntryEntity
        {
            GoalId = goalId,
            Seq = 0,
            Role = "user",
            Content = "caller-pending",
        };
        context.ConversationEntries.Add(callerEntry);

        var pipeline = NewPipeline(goalId);
        pipeline.Conversation.Add(new ConversationEntry("user", "full-save-entry"));
        pipeline.AdvanceTo(GoalPhase.Coding);

        var thrown = Record.Exception(() => FullSave(store, pipeline, ownershipRoute));

        Assert.NotNull(thrown);
        Assert.Contains(sentinel, EnumerateChain(thrown!));

        // THE CALLER'S ENTRY SURVIVES — still tracked, still pending (nothing discarded it).
        context.ChangeTracker.DetectChanges();
        Assert.Contains(
            context.ChangeTracker.Entries<ConversationEntryEntity>(),
            e => ReferenceEquals(e.Entity, callerEntry) && e.State == EntityState.Added);

        // …and it still flushes on the caller's own next save.
        pipeline.AdvanceTo(GoalPhase.Testing);
        store.SavePipelineState(pipeline);

        Assert.Equal("caller-pending", Assert.Single(ReadConversation(goalId)).Content);
    }

    // ═══════════════════════════ (4) the blast radius ════════════════════════════════════════════

    /// <summary>
    /// THE BLAST RADIUS: the cleanup discards ONLY the failing goal's conversation tracking. Another
    /// goal's pending conversation entry, an unrelated pending task-mapping row and an unrelated
    /// pending pipeline modification all survive with their values intact and really flush afterwards.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullSave_BorrowedContext_DiscardsOnlyTheFailingGoalsConversationTracking(bool ownershipRoute)
    {
        const string failingGoalId = "conv-blast-failing";
        const string otherGoalId = "conv-blast-other";
        var sentinel = new InvalidOperationException("blast-radius-sentinel");

        SeedPipelineWithConversation(failingGoalId, ("user", "original-1"));
        SeedPipelineWithConversation(otherGoalId);

        var context = CreateContext(new OneShotPipelinesDmlThrowInterceptor(sentinel));
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        // UNRELATED pending work on the SAME borrowed context.
        var otherConversation = new ConversationEntryEntity
        {
            GoalId = otherGoalId,
            Seq = 0,
            Role = "user",
            Content = "other-goal-pending",
        };
        context.ConversationEntries.Add(otherConversation);

        var otherMapping = new TaskMappingEntity { TaskId = "blast-task", GoalId = otherGoalId };
        context.TaskMappings.Add(otherMapping);

        var otherPipeline = context.Pipelines.Find(otherGoalId);
        Assert.NotNull(otherPipeline);
        otherPipeline!.Description = "other-goal-pending-description";

        var pipeline = NewPipeline(failingGoalId);
        pipeline.Conversation.Add(new ConversationEntry("user", "replacement-1"));
        pipeline.AdvanceTo(GoalPhase.Coding);

        var thrown = Record.Exception(() => FullSave(store, pipeline, ownershipRoute));

        Assert.NotNull(thrown);
        context.ChangeTracker.DetectChanges();

        // THE FAILING GOAL'S CONVERSATION TRACKING IS GONE (the cleanup ran and detached all of it).
        Assert.False(HasTrackedConversation(context, failingGoalId),
            "the failed full save left the rejected conversation replacement tracked");

        // …while EVERY unrelated pending entry is still tracked with its state intact.
        Assert.Contains(
            context.ChangeTracker.Entries<ConversationEntryEntity>(),
            e => ReferenceEquals(e.Entity, otherConversation) && e.State == EntityState.Added);
        Assert.Contains(
            context.ChangeTracker.Entries<TaskMappingEntity>(),
            e => ReferenceEquals(e.Entity, otherMapping) && e.State == EntityState.Added);
        Assert.Contains(
            context.ChangeTracker.Entries<PipelineEntity>(),
            e => ReferenceEquals(e.Entity, otherPipeline) && e.State == EntityState.Modified);

        // THE PENDING VALUES REALLY FLUSH on the caller's next state-only save…
        pipeline.AdvanceTo(GoalPhase.Testing);
        store.SavePipelineState(pipeline);

        Assert.Equal("other-goal-pending-description", RawScalar(
            "SELECT description FROM pipelines WHERE goal_id = $goal", ("$goal", otherGoalId)));
        Assert.Equal("other-goal-pending", Assert.Single(ReadConversation(otherGoalId)).Content);
        Assert.Equal(otherGoalId, RawScalar(
            "SELECT goal_id FROM task_mappings WHERE task_id = $task", ("$task", "blast-task")));

        // …and the failing goal's durable conversation is untouched.
        Assert.Equal("original-1", Assert.Single(ReadConversation(failingGoalId)).Content);
    }

    // ════════════════════ (5) the ownership contract, pinned ═════════════════════════════════════

    /// <summary>
    /// THE DOCUMENTED CONTRACT: once conversation replacement is ATTEMPTED, the operation owns the
    /// goal's conversation tracking scope, so the failure cleanup discards ALL tracked conversation
    /// state for that goal — the caller's own same-goal edit pending at that moment included. This
    /// vector exists so a future "preserve the caller's same-goal edits" change is a visible
    /// behaviour change rather than a silent one.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullSave_BorrowedContext_ReplacementAttempted_AlsoDiscardsCallerSameGoalConversationEdits(bool ownershipRoute)
    {
        const string goalId = "conv-same-goal-caller-goal";
        var sentinel = new InvalidOperationException("same-goal-caller-sentinel");

        SeedPipelineWithConversation(goalId, ("user", "original-1"));

        var context = CreateContext(new OneShotPipelinesDmlThrowInterceptor(sentinel));
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        // THE CALLER'S SAME-GOAL EDIT, pending on the borrowed context before the full save.
        var callerEntry = new ConversationEntryEntity
        {
            GoalId = goalId,
            Seq = 5,
            Role = "user",
            Content = "caller-same-goal-pending",
        };
        context.ConversationEntries.Add(callerEntry);

        var pipeline = NewPipeline(goalId);
        pipeline.Conversation.Add(new ConversationEntry("user", "replacement-1"));
        pipeline.AdvanceTo(GoalPhase.Coding);

        var thrown = Record.Exception(() => FullSave(store, pipeline, ownershipRoute));

        Assert.NotNull(thrown);
        // THE CONTRACT: the caller's same-goal edit was DISCARDED with the rest of the scope…
        Assert.False(HasTrackedConversation(context, goalId),
            "the ownership contract says the whole same-goal conversation scope is discarded");

        // …and the caller's next save cannot resurrect it.
        pipeline.AdvanceTo(GoalPhase.Testing);
        store.SavePipelineState(pipeline);

        Assert.Equal("original-1", Assert.Single(ReadConversation(goalId)).Content);
    }

    // ═══════════════ (5b) all tracked states and ordinal goal matching ═══════════════════════════

    /// <summary>
    /// ALL-STATE CONTRACT: a target-goal entry that is Modified or Unchanged at the instant cleanup
    /// begins is detached just like Added/Deleted entries. The replacement-add tracking hook
    /// deterministically restores the preloaded durable entry from Deleted to the requested state
    /// after SaveConversationCore has staged its deletes and at least one addition.
    /// </summary>
    [Theory]
    [InlineData(false, EntityState.Modified)]
    [InlineData(true, EntityState.Modified)]
    [InlineData(false, EntityState.Unchanged)]
    [InlineData(true, EntityState.Unchanged)]
    public void FullSave_BorrowedContext_CleanupDetachesTargetEntryInEveryTrackedState(
        bool ownershipRoute,
        EntityState stateAtCleanup)
    {
        const string goalId = "conv-all-states-goal";
        const string rejectedContent = "caller-modification-that-must-not-flush";
        var sentinel = new InvalidOperationException("all-states-sentinel");

        SeedPipelineWithConversation(goalId, ("user", "durable-original"));
        var durable = ReadConversation(goalId);

        var interceptor = new OneShotPipelinesDmlThrowInterceptor(sentinel);
        var context = CreateContext(interceptor);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);
        var targetEntry = context.ConversationEntries.Single(e => e.GoalId == goalId);
        Assert.Equal(EntityState.Unchanged, context.Entry(targetEntry).State);

        var triggerCount = 0;
        var matchingDetachCount = 0;
        context.ChangeTracker.Tracked += (_, args) =>
        {
            if (args.Entry.Entity is not ConversationEntryEntity added
                || args.Entry.State != EntityState.Added
                || added.Content != "replacement-trigger"
                || Interlocked.CompareExchange(ref triggerCount, 1, 0) != 0)
            {
                return;
            }

            if (stateAtCleanup == EntityState.Modified)
                targetEntry.Content = rejectedContent;
            context.Entry(targetEntry).State = stateAtCleanup;
        };
        context.ChangeTracker.StateChanged += (_, args) =>
        {
            if (ReferenceEquals(args.Entry.Entity, targetEntry)
                && args.OldState == stateAtCleanup
                && args.NewState == EntityState.Detached)
            {
                Interlocked.Increment(ref matchingDetachCount);
            }
        };

        var pipeline = NewPipeline(goalId);
        pipeline.Conversation.Add(new ConversationEntry("assistant", "replacement-trigger"));
        pipeline.AdvanceTo(GoalPhase.Coding);

        var thrown = Record.Exception(() => FullSave(store, pipeline, ownershipRoute));

        Assert.NotNull(thrown);
        Assert.Contains(sentinel, EnumerateChain(thrown!));
        Assert.Equal(1, interceptor.ThrowCount);
        Assert.Equal(1, triggerCount);
        Assert.Equal(1, matchingDetachCount);
        Assert.Equal(EntityState.Detached, context.Entry(targetEntry).State);

        pipeline.AdvanceTo(GoalPhase.Testing);
        store.SavePipelineState(pipeline);

        AssertConversationUnchanged(durable, ReadConversation(goalId));
        Assert.DoesNotContain(ReadConversation(goalId), e => e.Content == rejectedContent);
    }

    /// <summary>
    /// ORDINAL CONTRACT: an unrelated goal differing from the failed target only by case remains
    /// tracked as Modified and really flushes later. OrdinalIgnoreCase cleanup would detach it and
    /// fail both the tracking and durable-value assertions.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullSave_BorrowedContext_CleanupMatchesGoalIdOrdinally(bool ownershipRoute)
    {
        const string targetGoalId = "Conversation-Ordinal-Goal";
        const string caseVariantGoalId = "conversation-ordinal-goal";
        var sentinel = new InvalidOperationException("ordinal-comparison-sentinel");

        SeedPipelineWithConversation(targetGoalId, ("user", "target-original"));
        SeedPipelineWithConversation(caseVariantGoalId, ("user", "case-variant-original"));
        var targetDurable = ReadConversation(targetGoalId);

        var interceptor = new OneShotPipelinesDmlThrowInterceptor(sentinel);
        var context = CreateContext(interceptor);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);
        var caseVariantEntry = context.ConversationEntries.Single(e => e.GoalId == caseVariantGoalId);
        caseVariantEntry.Content = "case-variant-pending";
        context.ChangeTracker.DetectChanges();
        Assert.Equal(EntityState.Modified, context.Entry(caseVariantEntry).State);

        var pipeline = NewPipeline(targetGoalId);
        pipeline.Conversation.Add(new ConversationEntry("user", "target-replacement"));
        pipeline.AdvanceTo(GoalPhase.Coding);

        var thrown = Record.Exception(() => FullSave(store, pipeline, ownershipRoute));

        Assert.NotNull(thrown);
        Assert.Contains(sentinel, EnumerateChain(thrown!));
        Assert.Equal(1, interceptor.ThrowCount);
        Assert.Equal(EntityState.Modified, context.Entry(caseVariantEntry).State);
        Assert.Same(caseVariantEntry, context.ChangeTracker.Entries<ConversationEntryEntity>()
            .Single(e => e.Entity.GoalId == caseVariantGoalId).Entity);

        pipeline.AdvanceTo(GoalPhase.Testing);
        store.SavePipelineState(pipeline);

        Assert.Equal("case-variant-pending", Assert.Single(ReadConversation(caseVariantGoalId)).Content);
        AssertConversationUnchanged(targetDurable, ReadConversation(targetGoalId));
    }

    // ═══════════════════ (6) ordering: independent, and before the checkpoint hook ═══════════════

    /// <summary>
    /// THE ORDERING: on the ownership-aware full-save failure path the conversation cleanup runs
    /// INDEPENDENTLY — before the existing checkpoint hook, whose pipeline hygiene it must not
    /// replace. Both diagnostics are emitted, in that order.
    /// </summary>
    [Fact]
    public void FullSave_OwnershipRoute_ConversationCleanupRunsIndependentlyBeforeTheCheckpointHook()
    {
        const string goalId = "conv-order-goal";
        var sentinel = new InvalidOperationException("order-sentinel");

        SeedPipelineWithConversation(goalId, ("user", "original-1"));

        var context = CreateContext(new OneShotPipelinesDmlThrowInterceptor(sentinel));
        var logger = new TestLogger<PipelineStore>();
        var store = new PipelineStore(context, logger);

        var pipeline = NewPipeline(goalId);
        pipeline.Conversation.Add(new ConversationEntry("user", "replacement-1"));
        pipeline.AdvanceTo(GoalPhase.Coding);

        Assert.NotNull(Record.Exception(
            () => store.SavePipeline(pipeline, pipeline.CaptureAdmissionOwnership())));

        var cleanupIndex = logger.LogEntries.FindIndex(
            e => e.Message.Contains("full-save-conversation-cleanup", StringComparison.Ordinal));
        // The checkpoint hook's OWN diagnostic, matched by its own template prefix (the phrase
        // "tracker hygiene completed" appears in BOTH messages, so it cannot discriminate here).
        var hookIndex = logger.LogEntries.FindIndex(
            e => e.Message.Contains("WorkSlotIntegrity: ownership-checkpoint-cleanup", StringComparison.Ordinal));

        Assert.True(cleanupIndex >= 0, "the conversation cleanup diagnostic was never emitted");
        Assert.True(hookIndex >= 0, "the checkpoint hook's hygiene diagnostic was never emitted");
        Assert.True(cleanupIndex < hookIndex,
            "the conversation cleanup must run BEFORE the ownership-aware path's checkpoint hook | " +
            string.Join(" || ", logger.LogEntries.Select(e => e.Message)));
    }

    // ══════════════════ (7) the factory-owned context keeps its short-lived path ═════════════════

    /// <summary>
    /// THE OWNERSHIP GATE: a factory-owned context is discarded right after the operation, so the
    /// conversation-scope cleanup is NOT run for it — its diagnostic is absent. The borrowed-context
    /// vectors above are this gate's non-vacuous companion (the same failure DOES produce the
    /// diagnostic there).
    /// </summary>
    [Fact]
    public void FullSave_FactoryOwnedContext_FailedConversationReplacement_SkipsTheConversationCleanup()
    {
        const string goalId = "conv-factory-goal";
        var sentinel = new InvalidOperationException("factory-owned-sentinel");

        SeedPipelineWithConversation(goalId, ("user", "original-1"));
        var durable = ReadConversation(goalId);

        var factory = new TestFactory(_connectionString, new OneShotPipelinesDmlThrowInterceptor(sentinel));
        _factories.Add(factory);
        var logger = new TestLogger<PipelineStore>();
        var store = new PipelineStore(factory, logger);

        var pipeline = NewPipeline(goalId);
        pipeline.Conversation.Add(new ConversationEntry("user", "replacement-1"));
        pipeline.AdvanceTo(GoalPhase.Coding);

        Assert.NotNull(Record.Exception(() => store.SavePipeline(pipeline)));

        Assert.DoesNotContain(logger.LogEntries,
            e => e.Message.Contains("full-save-conversation-cleanup", StringComparison.Ordinal));
        AssertConversationUnchanged(durable, ReadConversation(goalId));
    }

    // ═══════════════════════ (8) the throwing logger, legacy route ═══════════════════════════════

    /// <summary>
    /// THE THROWING-LOGGER GUARD on the LEGACY full-save failure path: the guarded
    /// <c>LogError</c> keeps the EXACT database exception authoritative (the EF
    /// <see cref="DbUpdateException"/> wrapping the injected sentinel, by identity) while the
    /// conversation cleanup still runs — so the caller's next state-only save cannot flush the
    /// rejected replacement.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: an unguarded <c>_logger.LogError</c> lets the armed logger's own
    /// sentinel escape the catch instead of the rethrown database failure.
    /// </remarks>
    [Fact]
    public void FullSave_LegacyRoute_ThrowingLogger_KeepsOriginalExceptionAndStillCleansUp()
    {
        const string goalId = "conv-throwinglogger-goal";
        var sentinel = new InvalidOperationException("legacy-throwing-logger-sentinel");

        SeedPipelineWithConversation(goalId, ("user", "original-1"));
        var durable = ReadConversation(goalId);

        var interceptor = new OneShotPipelinesDmlThrowInterceptor(sentinel);
        var context = CreateContext(interceptor);
        var logger = new ThrowingStoreLogger();
        var store = new PipelineStore(context, logger);
        logger.Arm();   // the throw starts AFTER the constructor's logging

        var pipeline = NewPipeline(goalId);
        pipeline.Conversation.Add(new ConversationEntry("user", "replacement-1"));
        pipeline.AdvanceTo(GoalPhase.Coding);

        var thrown = Record.Exception(() => store.SavePipeline(pipeline));

        Assert.NotNull(thrown);
        var update = Assert.IsType<DbUpdateException>(thrown);
        var observedWrapper = Assert.IsType<DbUpdateException>(interceptor.SaveChangesFailure);
        Assert.Same(sentinel, update.InnerException);
        Assert.Same(observedWrapper, update);
        Assert.Same(observedWrapper, logger.CapturedException);
        Assert.Equal(1, interceptor.ThrowCount);
        Assert.True(logger.ThrowCount >= 1, "the guard's LogError attempt never reached the throwing logger");
        Assert.DoesNotContain(EnumerateChain(thrown!), e => ReferenceEquals(e, logger.LoggerSentinel));

        // THE CLEANUP STILL RAN…
        Assert.False(HasTrackedConversation(context, goalId),
            "the throwing logger must not skip the conversation-scope cleanup");

        // …so the rejected replacement cannot be flushed later.
        pipeline.AdvanceTo(GoalPhase.Testing);
        store.SavePipelineState(pipeline);

        AssertConversationUnchanged(durable, ReadConversation(goalId));
    }

    // ═══════════════════ (8b) the throwing logger, ownership-aware route ═════════════════════════

    /// <summary>
    /// THE THROWING-LOGGER GUARD on the OWNERSHIP-AWARE full-save failure path: the cleanup runs
    /// BEFORE the guarded diagnostics, so even with a logger throwing on EVERY armed write the
    /// EXACT database exception stays authoritative — the EF <see cref="DbUpdateException"/>
    /// wrapping the injected sentinel, BY IDENTITY — and the caller's next state-only save cannot
    /// flush the rejected replacement.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: an unguarded diagnostic (or the cleanup diagnostic escaping the
    /// guarded catch) lets the armed logger's own sentinel escape the catch instead of the rethrown
    /// database failure.
    /// </remarks>
    [Fact]
    public void FullSave_OwnershipRoute_ThrowingLogger_KeepsOriginalExceptionAndStillCleansUp()
    {
        const string goalId = "conv-throwinglogger-ownership-goal";
        var sentinel = new InvalidOperationException("ownership-throwing-logger-sentinel");

        SeedPipelineWithConversation(goalId, ("user", "original-1"));
        var durable = ReadConversation(goalId);

        var interceptor = new OneShotPipelinesDmlThrowInterceptor(sentinel);
        var context = CreateContext(interceptor);
        var logger = new ThrowingStoreLogger();
        var store = new PipelineStore(context, logger);
        logger.Arm();   // the throw starts AFTER the constructor's logging

        var pipeline = NewPipeline(goalId);
        pipeline.Conversation.Add(new ConversationEntry("user", "replacement-1"));
        pipeline.AdvanceTo(GoalPhase.Coding);

        var thrown = Record.Exception(
            () => store.SavePipeline(pipeline, pipeline.CaptureAdmissionOwnership()));

        Assert.NotNull(thrown);
        var update = Assert.IsType<DbUpdateException>(thrown);
        var observedWrapper = Assert.IsType<DbUpdateException>(interceptor.SaveChangesFailure);
        Assert.Same(sentinel, update.InnerException);
        Assert.Same(observedWrapper, update);
        Assert.Equal(1, interceptor.ThrowCount);
        Assert.True(logger.ThrowCount >= 1, "the guard's diagnostic attempt never reached the throwing logger");
        Assert.DoesNotContain(EnumerateChain(thrown!), e => ReferenceEquals(e, logger.LoggerSentinel));

        // THE CLEANUP STILL RAN…
        Assert.False(HasTrackedConversation(context, goalId),
            "the throwing logger must not skip the conversation-scope cleanup");

        // …so the rejected replacement cannot be flushed later.
        pipeline.AdvanceTo(GoalPhase.Testing);
        store.SavePipelineState(pipeline);

        AssertConversationUnchanged(durable, ReadConversation(goalId));
    }

    // ═══════════ (9) state-only saves never discard conversation work ═══════════════════════════

    /// <summary>
    /// THE SHARED-HELPER PROHIBITION on the LEGACY state-only route: it never attempts conversation
    /// replacement, so its failure path must NOT begin discarding conversation work — the caller's
    /// pending entry stays tracked and still flushes on the next save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE MUTATION THIS KILLS: calling the conversation cleanup from the shared
    /// <c>OnCheckpointSaveFailure</c> helper (or from the state-only catch) detaches the caller's
    /// pending conversation entry, so both the tracking probe and the flush probe fail.
    /// </para>
    /// <para>
    /// THIS VECTOR IS SCOPED TO THE LEGACY ROUTE ON PURPOSE. The ownership-aware state-only route is
    /// NOT covered here, because its failure path does reach the conversation entries — but not
    /// through the cleanup this change introduces: that route's pre-existing
    /// <c>OnCheckpointSaveFailure</c> detaches the tracked pipeline entry, and EF then cascades that
    /// detach to the entry's tracked dependents. So the ownership-aware state-only route already had
    /// that property before this change; asserting it here would be asserting a different, unrelated
    /// behaviour. See the raised issue for the observation.
    /// </para>
    /// </remarks>
    [Fact]
    public void StateOnlySave_LegacyRoute_BorrowedContext_Failure_DoesNotDiscardCallerConversationWork()
    {
        const string goalId = "conv-state-only-goal";
        var sentinel = new InvalidOperationException("state-only-legacy-sentinel");

        SeedPipelineWithConversation(goalId);

        var context = CreateContext(new OneShotPipelinesDmlThrowInterceptor(sentinel));
        var logger = new TestLogger<PipelineStore>();
        var store = new PipelineStore(context, logger);

        var callerEntry = new ConversationEntryEntity
        {
            GoalId = goalId,
            Seq = 0,
            Role = "user",
            Content = "caller-pending-on-state-save",
        };
        context.ConversationEntries.Add(callerEntry);

        var pipeline = NewPipeline(goalId);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var thrown = Record.Exception(() => store.SavePipelineState(pipeline));

        Assert.NotNull(thrown);
        context.ChangeTracker.DetectChanges();

        // NO conversation cleanup ran on the state-only path…
        Assert.DoesNotContain(logger.LogEntries,
            e => e.Message.Contains("full-save-conversation-cleanup", StringComparison.Ordinal));
        Assert.Contains(
            context.ChangeTracker.Entries<ConversationEntryEntity>(),
            e => ReferenceEquals(e.Entity, callerEntry) && e.State == EntityState.Added);

        // …so the caller's entry still flushes.
        pipeline.AdvanceTo(GoalPhase.Testing);
        store.SavePipelineState(pipeline);

        Assert.Equal("caller-pending-on-state-save", Assert.Single(ReadConversation(goalId)).Content);
    }

    /// <summary>
    /// THE SAME PROHIBITION on the OWNERSHIP-AWARE state-only route: whatever its pre-existing
    /// pipeline-entry hygiene does, the NEW full-save conversation-scope cleanup must never run.
    /// This test proves that narrow diagnostic boundary only; it deliberately makes no claim about
    /// conversation-entry tracking after the pre-existing checkpoint cleanup.
    /// </summary>
    /// <remarks>
    /// This pins the "do NOT add conversation cleanup to that shared helper" rule where it is
    /// observable WITHOUT depending on the unrelated dependent-detachment cascade described on the
    /// legacy vector above.
    /// </remarks>
    [Fact]
    public void StateOnlySave_OwnershipRoute_BorrowedContext_Failure_NeverRunsTheConversationCleanup()
    {
        const string goalId = "conv-state-only-ownership-goal";
        var sentinel = new InvalidOperationException("state-only-ownership-sentinel");

        SeedPipelineWithConversation(goalId);

        var context = CreateContext(new OneShotPipelinesDmlThrowInterceptor(sentinel));
        var logger = new TestLogger<PipelineStore>();
        var store = new PipelineStore(context, logger);

        var pipeline = NewPipeline(goalId);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var thrown = Record.Exception(
            () => store.SavePipelineState(pipeline, pipeline.CaptureAdmissionOwnership()));

        Assert.NotNull(thrown);

        // THE SHARED HELPER AND THE STATE-ONLY CATCH EMIT NEITHER CLEANUP DIAGNOSTIC: the new
        // conversation-scope cleanup is a FULL-SAVE-ONLY step.
        Assert.DoesNotContain(logger.LogEntries,
            e => e.Message.Contains("full-save-conversation-cleanup", StringComparison.Ordinal));
        // …and the checkpoint hygiene diagnostic that IS the state save's contract still appeared.
        Assert.Contains(logger.LogEntries, e => e.Message.Contains(
            "WorkSlotIntegrity: ownership-checkpoint-cleanup", StringComparison.Ordinal));
    }

    // ═══════════════════ (10) a failing cleanup reports the context SUSPECT ══════════════════════

    /// <summary>
    /// THE SUSPECT BRANCH: when the conversation-scope cleanup ITSELF faults, the diagnostic reports
    /// the context as SUSPECT — not safely reusable — and the ORIGINAL full-save exception is still
    /// rethrown unchanged.
    /// </summary>
    /// <remarks>
    /// The cleanup fault is injected through EF's OWN public mechanism: a
    /// <c>ChangeTracker.StateChanged</c> subscriber armed to throw ONLY when a
    /// <see cref="ConversationEntryEntity"/> transitions to <see cref="EntityState.Detached"/> — i.e.
    /// exactly the detach step of the cleanup under test. It cannot misfire on the preceding
    /// <c>SaveChanges</c> (whose transitions are to Unchanged), so the fault lands deterministically
    /// inside the cleanup and nowhere else. No production seam is added for this.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullSave_BorrowedContext_ConversationCleanupFault_ReportsSuspectAndPreservesOriginal(bool ownershipRoute)
    {
        const string goalId = "conv-suspect-goal";
        var sentinel = new InvalidOperationException("suspect-primary-sentinel");
        var detachSentinel = new InvalidOperationException("conversation-detach-sentinel");

        SeedPipelineWithConversation(goalId, ("user", "original-1"));
        var durable = ReadConversation(goalId);

        var interceptor = new OneShotPipelinesDmlThrowInterceptor(sentinel);
        var context = CreateContext(interceptor);
        var logger = new TestLogger<PipelineStore>();
        var store = new PipelineStore(context, logger);

        // THE ARMED CLEANUP FAULT — EF's own state-change event, narrowed to the cleanup's detach of
        // a CONVERSATION entry, so the preceding SaveChanges' transitions cannot trigger it.
        var detachFaults = 0;
        context.ChangeTracker.StateChanged += (_, args) =>
        {
            if (args.NewState == EntityState.Detached && args.Entry.Entity is ConversationEntryEntity)
            {
                Interlocked.Increment(ref detachFaults);
                throw detachSentinel;
            }
        };

        var pipeline = NewPipeline(goalId);
        pipeline.Conversation.Add(new ConversationEntry("user", "replacement-1"));
        pipeline.AdvanceTo(GoalPhase.Coding);

        var thrown = Record.Exception(() => FullSave(store, pipeline, ownershipRoute));

        // THE FAULT REALLY LANDED IN THE CLEANUP (and was swallowed by its guard).
        Assert.Equal(1, detachFaults);

        // THE PRIMARY EXCEPTION IS PRESERVED BY IDENTITY — never the cleanup fault.
        Assert.NotNull(thrown);
        var update = Assert.IsType<DbUpdateException>(thrown);
        var observedWrapper = Assert.IsType<DbUpdateException>(interceptor.SaveChangesFailure);
        Assert.Same(sentinel, update.InnerException);
        Assert.Same(observedWrapper, update);
        Assert.DoesNotContain(EnumerateChain(thrown!), e => ReferenceEquals(e, detachSentinel));

        // …and the SUSPECT diagnostic really named the context as not safely reusable.
        var suspect = Assert.Single(logger.LogEntries, e => e.Message.Contains(
            "full-save-conversation-cleanup", StringComparison.Ordinal)
            && e.Message.Contains("SUSPECT", StringComparison.Ordinal));
        Assert.Contains(goalId, suspect.Message, StringComparison.Ordinal);
        Assert.Contains("conversation-detach-sentinel", suspect.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(
            "conversation replacement's tracker hygiene completed", StringComparison.Ordinal));

        // The durable conversation is untouched (nothing was ever flushed).
        AssertConversationUnchanged(durable, ReadConversation(goalId));
    }

    // ══════════════════════════════════ test seams and helpers ═══════════════════════════════════

    /// <summary>
    /// Throws the supplied sentinel before the FIRST pipelines DML statement (INSERT/UPDATE) of the
    /// batch, then lets every later statement through — the one-shot pre-commit failure the leak
    /// chain needs. BOTH statement paths are hooked: the SQLite provider emits an UPDATE as
    /// <c>UPDATE … RETURNING 1</c> through the READER path.
    /// </summary>
    private sealed class OneShotPipelinesDmlThrowInterceptor(Exception sentinel)
        : DbCommandInterceptor, ISaveChangesInterceptor
    {
        private int _fired;
        private int _throwCount;

        public int ThrowCount => Volatile.Read(ref _throwCount);

        /// <summary>The exact outer EF exception observed before it propagates from SaveChanges.</summary>
        public Exception? SaveChangesFailure { get; private set; }

        public void SaveChangesFailed(DbContextErrorEventData eventData) =>
            SaveChangesFailure = eventData.Exception;

        private void ThrowIfTargeted(DbCommand command)
        {
            var text = command.CommandText;
            var trimmed = text.TrimStart();
            var isDml = trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase);
            if (!isDml || !text.Contains("pipelines", StringComparison.OrdinalIgnoreCase))
                return;
            if (Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
                return;

            Interlocked.Increment(ref _throwCount);
            throw sentinel;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            ThrowIfTargeted(command);
            return result;
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            ThrowIfTargeted(command);
            return result;
        }
    }

    /// <summary>
    /// Throws the supplied sentinel at the FIRST <c>SELECT … FROM "pipelines"</c> read — the upsert's
    /// lookup, i.e. strictly BEFORE <c>SaveConversationCore</c> is reached (the attempt-gate seam).
    /// </summary>
    private sealed class OneShotPipelinesSelectThrowInterceptor(Exception sentinel) : DbCommandInterceptor
    {
        private int _fired;
        private int _throwCount;

        public int ThrowCount => Volatile.Read(ref _throwCount);

        private void ThrowIfTargeted(DbCommand command)
        {
            var text = command.CommandText;
            if (!text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                return;
            if (!text.Contains("pipelines", StringComparison.OrdinalIgnoreCase))
                return;
            if (Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
                return;

            Interlocked.Increment(ref _throwCount);
            throw sentinel;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            ThrowIfTargeted(command);
            return result;
        }
    }

    /// <summary>A logger that THROWS on EVERY write once armed — the guard's competing failure.</summary>
    private sealed class ThrowingStoreLogger : ILogger<PipelineStore>
    {
        private bool _armed;
        private int _throwCount;

        /// <summary>The DISTINCT instance every armed write throws (identity-detectable).</summary>
        public InvalidOperationException LoggerSentinel { get; } = new("the store logger itself threw SENTINEL");

        public int ThrowCount => Volatile.Read(ref _throwCount);

        /// <summary>The exact non-null exception argument most recently offered to the logger.</summary>
        public Exception? CapturedException { get; private set; }

        public void Arm() => _armed = true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!_armed)
                return;

            if (exception is not null)
                CapturedException = exception;

            Interlocked.Increment(ref _throwCount);
            throw LoggerSentinel;
        }
    }

    /// <summary>A real factory handing out contexts over the shared database (the ownsContext path).</summary>
    private sealed class TestFactory(string connectionString, IInterceptor? interceptor)
        : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly List<CopilotHiveDbContext> _created = [];
        private readonly List<SqliteConnection> _connections = [];

        public CopilotHiveDbContext CreateDbContext()
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            _connections.Add(connection);

            var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
            if (interceptor is not null)
                builder.AddInterceptors(interceptor);

            var context = new CopilotHiveDbContext(builder.Options);
            _created.Add(context);
            return context;
        }

        public void Dispose()
        {
            foreach (var context in _created)
                context.Dispose();
            foreach (var connection in _connections)
                connection.Dispose();
        }
    }
}
