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

    private CopilotHiveDbContext CreateContext(params IInterceptor[] interceptors)
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        _connections.Add(connection);

        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);

        var context = new CopilotHiveDbContext(builder.Options);
        _contexts.Add(context);
        return context;
    }

    private PipelineStore CreateStore(params IInterceptor[] interceptors) =>
        new(CreateContext(interceptors), NullLogger<PipelineStore>.Instance);

    private PipelineStore CreateStore(ILogger<PipelineStore> logger, params IInterceptor[] interceptors) =>
        new(CreateContext(interceptors), logger);

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

    /// <summary>THE DURABLE REGISTRY BLOB the existing-row vectors seed and then preserve: a real
    /// encoded payload naming a task the REJECTED writes never carry, so a leaked rejected blob is
    /// detectable by content as well as by byte equality.</summary>
    private static readonly string DurableBlob = WorkSlotRegistryCodec.Encode(new WorkSlotRegistrySnapshot(
        [new WorkSlotView(new WorkSlot("state-only-durable-task", new WorkSlotPosition(1, GoalPhase.Coding, 1), 1),
            WorkSlotState.Recorded)],
        [new WorkSlotRegistryAttemptEntry(new WorkSlotPosition(1, GoalPhase.Coding, 1), 1)]));

    /// <summary>Writes a RAW blob (and the durable description/pointer the existing-row vectors
    /// assert against) directly onto the seeded row — no EF, no tracker.</summary>
    private void SeedBlob(string goalId, string? blob)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText =
            """
            UPDATE pipelines
            SET work_slot_registry_json = $blob,
                description = 'durable-description',
                active_task_id = 'state-only-durable-task'
            WHERE goal_id = $goal
            """;
        command.Parameters.AddWithValue("$goal", goalId);
        command.Parameters.AddWithValue("$blob", (object?)blob ?? DBNull.Value);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    /// <summary>Runs a raw non-query on the keeper connection (the out-of-band state change).</summary>
    private void ExecuteOnKeeper(string sql)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>The raw <c>work_slot_registry_json</c> text, read through the keeper (no EF).</summary>
    private string? RawBlob(string goalId) => RawScalar(
        "SELECT work_slot_registry_json FROM pipelines WHERE goal_id = $goal", ("$goal", goalId));

    /// <summary>The raw <c>pipelines</c> row count for a goal — the "the rejected insert never landed"
    /// probe.</summary>
    private long RawScalarCount(string goalId)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pipelines WHERE goal_id = $goal";
        command.Parameters.AddWithValue("$goal", goalId);
        return (long)command.ExecuteScalar()!;
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
    /// THIS VECTOR IS SCOPED TO THE LEGACY ROUTE ON PURPOSE, and pins ONE narrow boundary: the LEGACY
    /// state-only failure path never runs the full-save conversation-scope cleanup. The
    /// ownership-aware state-only path — whose failure DOES reach the checkpoint helper — is covered
    /// separately by the <c>StateOnlySave_OwnershipRoute_*</c> vectors below. Those vectors now
    /// describe the CURRENT reload-in-place behavior: the helper RELOADS an existing Unchanged/
    /// Modified principal via <c>EntityEntry.Reload</c> on the same entry instead of detaching it, so
    /// the caller's pending conversation work (and other dependents) is no longer cascaded away, and
    /// they assert the same-object/Unchanged/flush outcome that behavior produces. The detach-cascade
    /// this paragraph used to describe was the pre-change behavior, not the current one.
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
    /// THE REAL REPRODUCTION OF THE REPORTED DEFECT, on the OWNERSHIP-AWARE state-only route: an
    /// EXISTING durable pipeline row is loaded onto the borrowed context (a HELD principal
    /// reference), the caller stages its OWN same-goal conversation entry, and the checkpoint's
    /// pre-commit row write fails. Because the cleanup RELOADS THE EXISTING TRACKED PIPELINE IN
    /// PLACE instead of detaching/re-finding it, the SAME principal reference comes back Unchanged
    /// with the DURABLE pointer/registry, the EXACT conversation object keeps its pending content
    /// and Added state, and the EXACT outer EF exception was propagated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE DEFECT THIS PINS, OBSERVED RATHER THAN ASSUMED: the previous cleanup detached the
    /// principal, and EF's detach CASCADES to the principal's tracked dependents — the caller's
    /// Added conversation work was detached with it, so the SAME instance assertion, the Added-state
    /// assertion and the follow-up flush all fail (the probe run that established this is what the
    /// transition probe below re-checks at runtime). Note that only some dependent states cascade;
    /// the assertions here name the state that actually does, and the Modified/Deleted companions
    /// below cover the states whose dependent tracking is retained.
    /// </para>
    /// <para>
    /// THE TRANSITIONS ARE OBSERVED: the principal must be seen going Unchanged → Modified (the
    /// rejected staging) and then back to Unchanged (the reload), and the conversation dependent must
    /// NOT be seen transitioning to Detached — so a detach-based cleanup cannot satisfy this vector.
    /// </para>
    /// <para>
    /// "STATE-ONLY" MEANS ONLY THAT THE ROUTE NEVER REPLACES THE CONVERSATION: its context-wide
    /// <c>SaveChanges</c> flushes the caller's surviving pending conversation work on the following
    /// successful save, which the final half of this vector proves.
    /// </para>
    /// </remarks>
    [Fact]
    public void StateOnlySave_OwnershipRoute_ExistingRowPreCommitFailure_PreservesHeldPrincipalAndCallerConversation()
    {
        const string goalId = "conv-state-only-existing-row-goal";
        var sentinel = new InvalidOperationException("state-only-existing-row-sentinel");

        // The durable row AND its durable blob, seeded through a SEPARATE context.
        SeedPipelineWithConversation(goalId, ("user", "durable-entry"));
        var durableConversation = ReadConversation(goalId);
        SeedBlob(goalId, DurableBlob);

        var interceptor = new OneShotPipelinesDmlThrowInterceptor(sentinel);
        var context = CreateContext(interceptor);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        // THE HELD PRINCIPAL REFERENCE — the object the caller observes.
        var principal = context.Pipelines.Find(goalId);
        Assert.NotNull(principal);
        principal!.Description = "caller-pending-description";
        Assert.Equal(EntityState.Modified, context.Entry(principal).State);

        // THE CALLER'S OWN same-goal conversation work, pending BEFORE the state save.
        var callerEntry = new ConversationEntryEntity
        {
            GoalId = goalId,
            Seq = 5,
            Role = "user",
            Content = "caller-pending-on-state-save",
        };
        context.ConversationEntries.Add(callerEntry);
        context.ChangeTracker.DetectChanges();
        Assert.Equal(EntityState.Added, context.Entry(callerEntry).State);

        var principalTransitions = new List<(EntityState From, EntityState To)>();
        var conversationDetaches = 0;
        context.ChangeTracker.StateChanged += (_, args) =>
        {
            if (ReferenceEquals(args.Entry.Entity, principal))
                principalTransitions.Add((args.OldState, args.NewState));
            if (args.NewState == EntityState.Detached && args.Entry.Entity is ConversationEntryEntity)
                Interlocked.Increment(ref conversationDetaches);
        };

        // The capture is taken off a pipeline carrying a NON-NULL pointer and a NON-EMPTY registry,
        // so the rejected write genuinely had a pointer/registry to stage.
        var pipeline = NewPipeline(goalId);
        pipeline.SetActiveTask("state-only-rejected-task");

        var thrown = Record.Exception(
            () => store.SavePipelineState(pipeline, pipeline.CaptureAdmissionOwnership()));

        Assert.NotNull(thrown);
        // THE EXACT OUTER EF EXCEPTION, by identity: the DbUpdateException wrapping the injected
        // sentinel, not the cleanup's own failure and not a re-created wrapper.
        var update = Assert.IsType<DbUpdateException>(thrown);
        Assert.Same(sentinel, update.InnerException);
        Assert.Same(interceptor.SaveChangesFailure, update);
        Assert.Equal(1, interceptor.ThrowCount);

        context.ChangeTracker.DetectChanges();

        // ── THE SAME PRINCIPAL, UNCHANGED, CARRYING THE DURABLE POINTER/BLOB ──
        Assert.Equal(EntityState.Unchanged, context.Entry(principal).State);
        Assert.Same(principal, context.Pipelines.Find(goalId));   // not detached and re-found
        Assert.Equal("state-only-durable-task", principal.ActiveTaskId);
        Assert.Equal(DurableBlob, principal.WorkSlotRegistryJson);
        Assert.Equal("durable-description", principal.Description);  // the pending scalar edit is discarded
        Assert.Equal(DurableBlob, RawBlob(goalId));

        // ── THE EXACT CONVERSATION OBJECT, STILL PENDING AND STILL Added ──
        Assert.Contains(
            context.ChangeTracker.Entries<ConversationEntryEntity>(),
            e => ReferenceEquals(e.Entity, callerEntry) && e.State == EntityState.Added);
        Assert.Equal("caller-pending-on-state-save", callerEntry.Content);
        Assert.Equal(0, Volatile.Read(ref conversationDetaches));

        // ── THE TRANSITIONS REALLY HAPPENED (an observed defect, not an assumption) ──
        // The principal sat Modified after the caller's pending edit, so the ONLY transition the
        // cleanup can produce is Modified → Unchanged (the reload, read while the entry was still
        // attached). A detach-based cleanup would show Modified → Detached instead, which is exactly
        // the defect this vector pins — so BOTH halves are asserted, not just the absence of Detached.
        Assert.Equal((EntityState.Modified, EntityState.Unchanged), Assert.Single(principalTransitions));
        Assert.DoesNotContain(principalTransitions, t => t.To == EntityState.Detached);

        // ── THE SURVIVING WORK REALLY FLUSHES on a plain SaveChanges, with NO re-tracking by the
        //    test, while the restored durable pointer/blob stay exactly as they were. ──
        context.SaveChanges();

        var afterFlush = ReadConversation(goalId);
        Assert.Equal("caller-pending-on-state-save", Assert.Single(afterFlush, r => r.Seq == 5).Content);
        // The pre-existing durable entry is untouched by the surviving caller work.
        Assert.Equal(durableConversation, afterFlush.Where(r => r.Seq == 0).ToList());
        Assert.Equal("state-only-durable-task", RawScalar(
            "SELECT active_task_id FROM pipelines WHERE goal_id = $goal", ("$goal", goalId)));
        Assert.Equal(DurableBlob, RawBlob(goalId));
    }

    /// <summary>
    /// THE MODIFIED/DELETED COMPANIONS: with an EXISTING principal (Unchanged), a caller conversation
    /// entry already durable and now Modified keeps its pending value and its Modified property
    /// metadata, and a durable entry marked Deleted stays Deleted — through the failed ownership-aware
    /// state-only save — and the following plain <c>SaveChanges</c> lands the intended update and
    /// delete. The durable pointer/blob are untouched by that flush.
    /// </summary>
    [Fact]
    public void StateOnlySave_OwnershipRoute_ExistingRow_ModifiedAndDeletedConversationWorkSurvives()
    {
        const string goalId = "conv-state-only-dep-states";
        var sentinel = new InvalidOperationException("state-only-dep-states-sentinel");

        SeedPipelineWithConversation(goalId, ("user", "durable-0"), ("assistant", "durable-1"));
        SeedBlob(goalId, DurableBlob);

        var interceptor = new OneShotPipelinesDmlThrowInterceptor(sentinel);
        var context = CreateContext(interceptor);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        var durable = context.ConversationEntries.Where(e => e.GoalId == goalId).OrderBy(e => e.Seq).ToList();
        var modified = durable[0];
        var deleted = durable[1];
        modified.Content = "modified-pending";
        context.ConversationEntries.Remove(deleted);
        var added = new ConversationEntryEntity
        {
            GoalId = goalId,
            Seq = 9,
            Role = "user",
            Content = "added-pending",
        };
        context.ConversationEntries.Add(added);
        context.ChangeTracker.DetectChanges();

        var principal = context.Pipelines.Find(goalId);
        Assert.NotNull(principal);

        var pipeline = NewPipeline(goalId);
        pipeline.SetActiveTask("rejected-dep-states-task");

        var thrown = Record.Exception(
            () => store.SavePipelineState(pipeline, pipeline.CaptureAdmissionOwnership()));

        Assert.NotNull(thrown);
        context.ChangeTracker.DetectChanges();

        // THE PRINCIPAL IS RELOADED IN PLACE (the pre-existing row it must not be detached from).
        Assert.Equal(EntityState.Unchanged, context.Entry(principal!).State);
        Assert.Same(principal, context.Pipelines.Find(goalId));

        // Every dependent keeps the EXACT object, state and pending property metadata.
        Assert.Same(modified, context.ChangeTracker.Entries<ConversationEntryEntity>()
            .Single(e => ReferenceEquals(e.Entity, modified)).Entity);
        Assert.Equal(EntityState.Modified, context.Entry(modified).State);
        Assert.Equal("modified-pending", modified.Content);
        Assert.True(context.Entry(modified).Property(e => e.Content).IsModified);
        Assert.Equal("durable-0", context.Entry(modified).Property(e => e.Content).OriginalValue);

        Assert.Equal(EntityState.Deleted, context.Entry(deleted).State);
        Assert.Equal(EntityState.Added, context.Entry(added).State);
        Assert.True(context.Entry(added).Property(e => e.Id).IsTemporary);

        // THE INTENDED WORK LANDS — no re-tracking by the test, just a plain SaveChanges.
        context.SaveChanges();

        var rows = ReadConversation(goalId);
        Assert.Equal("modified-pending", Assert.Single(rows, r => r.Seq == 0).Content);
        Assert.Equal("added-pending", Assert.Single(rows, r => r.Seq == 9).Content);
        Assert.DoesNotContain(rows, r => r.Content == "durable-1");

        // The restored durable pointer/blob are exactly the originals (the rejected checkpoint never
        // reached a flush, and the surviving conversation work did not disturb the row).
        Assert.Equal(DurableBlob, RawBlob(goalId));
    }

    /// <summary>
    /// THE LATER LEGACY SAVE DOES NOT FLUSH THE REJECTED CHECKPOINT REGISTRY. After the
    /// ownership-aware state-only failure the principal is Unchanged at database truth, so a later
    /// LEGACY state save on the SAME context cannot smuggle the rejected blob through.
    /// </summary>
    /// <remarks>
    /// The legacy save legitimately writes ITS OWN current live pointer, so the assertion is scoped
    /// to the ONE thing the rejected write would have changed: the registry blob stays the durable
    /// one (it never contains the rejected task id).
    /// </remarks>
    [Fact]
    public void StateOnlySave_OwnershipRoute_ExistingRow_ThenLegacySave_DoesNotFlushTheRejectedRegistry()
    {
        const string goalId = "conv-state-only-then-legacy-goal";
        var sentinel = new InvalidOperationException("state-only-then-legacy-sentinel");

        SeedPipelineWithConversation(goalId, ("user", "durable-entry"));
        SeedBlob(goalId, DurableBlob);

        var interceptor = new OneShotPipelinesDmlThrowInterceptor(sentinel);
        var context = CreateContext(interceptor);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        var principal = context.Pipelines.Find(goalId);
        Assert.NotNull(principal);

        var pipeline = NewPipeline(goalId);
        pipeline.SetActiveTask("rejected-legacy-task");
        Assert.NotEqual(DurableBlob, WorkSlotRegistryCodec.Encode(pipeline.CaptureRegistry()));

        var thrown = Record.Exception(
            () => store.SavePipelineState(pipeline, pipeline.CaptureAdmissionOwnership()));
        Assert.NotNull(thrown);
        context.ChangeTracker.DetectChanges();
        Assert.Equal(EntityState.Unchanged, context.Entry(principal!).State);

        // THE LATER LEGACY SAVE, on the SAME borrowed context.
        pipeline.AdvanceTo(GoalPhase.Testing);
        store.SavePipelineState(pipeline);

        Assert.Equal("Testing", RawScalar(
            "SELECT phase FROM pipelines WHERE goal_id = $goal", ("$goal", goalId)));
        Assert.Equal(DurableBlob, RawBlob(goalId));
        Assert.DoesNotContain("rejected-legacy-task", RawBlob(goalId) ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("durable-entry", Assert.Single(ReadConversation(goalId)).Content);
    }

    /// <summary>
    /// THE BLAST RADIUS, on the EXISTING-ROW cleanup: a case-distinct OTHER goal's pending
    /// conversation entry and an unrelated pending task-mapping row keep their state and values and
    /// really flush afterwards, while the affected goal's principal is reloaded in place.
    /// </summary>
    [Fact]
    public void StateOnlySave_OwnershipRoute_ExistingRow_LeavesOtherGoalsAndUnrelatedWorkUntouched()
    {
        const string targetGoalId = "Conv-State-Only-Target";
        const string caseVariantGoalId = "conv-state-only-target";
        var sentinel = new InvalidOperationException("state-only-blast-sentinel");

        SeedPipelineWithConversation(targetGoalId, ("user", "target-durable"));
        SeedPipelineWithConversation(caseVariantGoalId);
        SeedBlob(targetGoalId, DurableBlob);

        var interceptor = new OneShotPipelinesDmlThrowInterceptor(sentinel);
        var context = CreateContext(interceptor);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        var caseVariantEntry = new ConversationEntryEntity
        {
            GoalId = caseVariantGoalId,
            Seq = 0,
            Role = "user",
            Content = "case-variant-pending",
        };
        context.ConversationEntries.Add(caseVariantEntry);

        var unrelatedMapping = new TaskMappingEntity { TaskId = "state-only-blast-task", GoalId = caseVariantGoalId };
        context.TaskMappings.Add(unrelatedMapping);

        var principal = context.Pipelines.Find(targetGoalId);
        Assert.NotNull(principal);

        var pipeline = NewPipeline(targetGoalId);
        pipeline.SetActiveTask("rejected-blast-task");

        var thrown = Record.Exception(
            () => store.SavePipelineState(pipeline, pipeline.CaptureAdmissionOwnership()));
        Assert.NotNull(thrown);
        context.ChangeTracker.DetectChanges();

        // The affected principal is reloaded in place…
        Assert.Equal(EntityState.Unchanged, context.Entry(principal!).State);
        Assert.Same(principal, context.Pipelines.Find(targetGoalId));

        // …while every unrelated pending entry is still tracked with its state intact.
        Assert.Contains(
            context.ChangeTracker.Entries<ConversationEntryEntity>(),
            e => ReferenceEquals(e.Entity, caseVariantEntry) && e.State == EntityState.Added);
        Assert.Contains(
            context.ChangeTracker.Entries<TaskMappingEntity>(),
            e => ReferenceEquals(e.Entity, unrelatedMapping) && e.State == EntityState.Added);

        // …and really flushes on the next plain save.
        context.SaveChanges();

        Assert.Equal("case-variant-pending", Assert.Single(ReadConversation(caseVariantGoalId)).Content);
        Assert.Equal(caseVariantGoalId, RawScalar(
            "SELECT goal_id FROM task_mappings WHERE task_id = $task", ("$task", "state-only-blast-task")));
        Assert.Equal(DurableBlob, RawBlob(targetGoalId));
        Assert.Equal("target-durable", Assert.Single(ReadConversation(targetGoalId)).Content);
    }

    // ══════════ (9b) the existing-row cleanup's explicit safety boundaries ══════════

    /// <summary>
    /// THE ADDED-PRINCIPAL BOUNDARY: when the failing save's principal is Added (no durable row),
    /// the cleanup keeps the EXISTING rejection-DETACH rather than a blind reload — a reload of an
    /// Added entity with no row is a no-op that would leave the rejected checkpoint insertable, so
    /// this path claims NO dependent preservation. The primary exception is still preserved and the
    /// rejected checkpoint is gone from the tracker (a following plain save writes nothing).
    /// </summary>
    [Fact]
    public void StateOnlySave_OwnershipRoute_AddedPrincipal_RejectedCheckpointDetachedAndNotFlushable()
    {
        const string goalId = "conv-state-only-added-principal-goal";
        var sentinel = new InvalidOperationException("state-only-added-principal-sentinel");

        var interceptor = new OneShotPipelinesDmlThrowInterceptor(sentinel);
        var context = CreateContext(interceptor);
        var logger = new TestLogger<PipelineStore>();
        var store = new PipelineStore(context, logger);

        var pipeline = NewPipeline(goalId);
        pipeline.SetActiveTask("rejected-added-task");

        var thrown = Record.Exception(
            () => store.SavePipelineState(pipeline, pipeline.CaptureAdmissionOwnership()));

        Assert.NotNull(thrown);
        var update = Assert.IsType<DbUpdateException>(thrown);
        Assert.Same(sentinel, update.InnerException);

        context.ChangeTracker.DetectChanges();
        // THE REJECTION-DETACH: no pipeline entry survives in a flushable state…
        Assert.DoesNotContain(
            context.ChangeTracker.Entries<PipelineEntity>(),
            e => e.State is EntityState.Added or EntityState.Modified);
        // …and the cleanup reported COMPLETION (this is the detach path, not a fault).
        Assert.Contains(logger.LogEntries, e => e.Message.Contains(
            "tracker hygiene completed", StringComparison.Ordinal));

        // A plain save cannot create the rejected row.
        Assert.Null(Record.Exception(() => context.SaveChanges()));
        Assert.Equal(0L, RawScalarCount(goalId));
        Assert.Null(RawScalar(
            "SELECT active_task_id FROM pipelines WHERE goal_id = $goal", ("$goal", goalId)));
    }

    /// <summary>
    /// THE MISSING-DURABLE-ROW BOUNDARY, on the REAL cleanup: the row is deleted behind the tracker
    /// before the failing save, so the reload finds nothing. EF detaches the principal itself, the
    /// rejected checkpoint is never left insertable, and the diagnostic HONESTLY reports that no
    /// preservation/reuse is claimed — never a successful-hygiene claim.
    /// </summary>
    [Fact]
    public void StateOnlySave_OwnershipRoute_DisappearedDurableRow_ReportsNoPreservationClaim()
    {
        const string goalId = "conv-state-only-missing-row-goal";
        var sentinel = new InvalidOperationException("state-only-missing-row-sentinel");

        SeedPipelineWithConversation(goalId, ("user", "durable-entry"));

        var interceptor = new OneShotPipelinesDmlThrowInterceptor(sentinel);
        var context = CreateContext(interceptor);
        var logger = new TestLogger<PipelineStore>();
        var store = new PipelineStore(context, logger);

        var principal = context.Pipelines.Find(goalId);
        Assert.NotNull(principal);

        // The durable row disappears from under the tracker.
        ExecuteOnKeeper("DELETE FROM pipelines WHERE goal_id = 'conv-state-only-missing-row-goal'");

        var pipeline = NewPipeline(goalId);
        pipeline.SetActiveTask("rejected-missing-task");

        var thrown = Record.Exception(
            () => store.SavePipelineState(pipeline, pipeline.CaptureAdmissionOwnership()));

        Assert.NotNull(thrown);
        var update = Assert.IsType<DbUpdateException>(thrown);
        Assert.Same(sentinel, update.InnerException);
        Assert.Equal(1, interceptor.ThrowCount);

        context.ChangeTracker.DetectChanges();
        // EF detached the principal on the missing row — no flushable rejected checkpoint remains…
        Assert.DoesNotContain(
            context.ChangeTracker.Entries<PipelineEntity>(),
            e => e.State is EntityState.Added or EntityState.Modified);

        // …and the diagnostic says so HONESTLY.
        Assert.Contains(logger.LogEntries, e => e.Message.Contains(
            "reload found NO durable row", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(
            "tracker hygiene completed", StringComparison.Ordinal));

        // A plain save writes nothing for the vanished row and does not resurrect the checkpoint.
        Assert.Null(Record.Exception(() => context.SaveChanges()));
        Assert.Null(RawScalar(
            "SELECT active_task_id FROM pipelines WHERE goal_id = $goal", ("$goal", goalId)));
    }

    /// <summary>
    /// THE THROWING-RELOAD BOUNDARY: the cleanup's own reload SELECT is failed after the row write
    /// failed. The targeted detach FALLBACK still removes the rejected checkpoint (so nothing can be
    /// flushed later), the ORIGINAL write exception is propagated by identity, and the diagnostic
    /// reports the reload failure WITHOUT claiming preservation or safe reuse.
    /// </summary>
    [Fact]
    public void StateOnlySave_OwnershipRoute_ReloadSelectFails_DetachFallbackHoldsAndReportsHonestly()
    {
        const string goalId = "conv-state-only-reload-fail-goal";
        var writeSentinel = new InvalidOperationException("state-only-write-sentinel");
        var selectSentinel = new InvalidOperationException("state-only-reload-sentinel");

        SeedPipelineWithConversation(goalId, ("user", "durable-entry"));
        SeedBlob(goalId, DurableBlob);

        var select = new ArmablePipelinesSelectThrowInterceptor(selectSentinel);
        var write = new OneShotPipelinesDmlThrowInterceptor(writeSentinel);
        var context = CreateContext(write, select);
        var logger = new TestLogger<PipelineStore>();
        var store = new PipelineStore(context, logger);

        var principal = context.Pipelines.Find(goalId);
        Assert.NotNull(principal);

        // The principal is ALREADY tracked, so the first armed `pipelines` SELECT is the cleanup's
        // reload; the separate one-shot interceptor fails the row write.
        select.Arm();

        var pipeline = NewPipeline(goalId);
        pipeline.SetActiveTask("rejected-reload-fail-task");

        var thrown = Record.Exception(
            () => store.SavePipelineState(pipeline, pipeline.CaptureAdmissionOwnership()));

        // THE PRIMARY EXCEPTION IS PRESERVED, by identity — never the reload failure.
        Assert.NotNull(thrown);
        var update = Assert.IsType<DbUpdateException>(thrown);
        Assert.Same(writeSentinel, update.InnerException);
        Assert.Same(write.SaveChangesFailure, update);
        Assert.DoesNotContain(EnumerateChain(thrown!), e => ReferenceEquals(e, selectSentinel));
        // THE RELOAD REALLY RAN AND REALLY FAILED (the fallback is not reached vacuously).
        Assert.Equal(1, select.ThrowCount);
        Assert.Equal(1, write.ThrowCount);

        context.ChangeTracker.DetectChanges();
        // THE FALLBACK HELD: the rejected checkpoint is off the tracker.
        Assert.DoesNotContain(
            context.ChangeTracker.Entries<PipelineEntity>(),
            e => e.State is EntityState.Added or EntityState.Modified);
        Assert.Equal(DurableBlob, RawBlob(goalId));
        // …and the diagnostic names the failed reload and denies the preservation claim.
        var diagnostic = Assert.Single(logger.LogEntries, e => e.Message.Contains(
            "the existing-row reload failed", StringComparison.Ordinal));
        Assert.Contains("state-only-reload-sentinel", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(
            "tracker hygiene completed", StringComparison.Ordinal));

        // The rejected blob cannot be flushed by a later save; the durable row keeps its blob.
        select.Disarm();   // the reload seam is one-shot in intent: the later save is not under test
        store.SavePipelineState(pipeline);
        Assert.Equal(DurableBlob, RawBlob(goalId));
        Assert.DoesNotContain("rejected-reload-fail-task", RawBlob(goalId) ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE THROWING-LOGGER BOUNDARY ON THE RELOAD PATH: a logger that throws on EVERY armed write
    /// cannot replace the primary exception (the diagnostics go through the file's existing no-throw
    /// helper), and cannot turn a COMPLETED cleanup into the SUSPECT branch either.
    /// </summary>
    [Fact]
    public void StateOnlySave_OwnershipRoute_ExistingRow_ThrowingLogger_KeepsTheWriteExceptionAuthoritative()
    {
        const string goalId = "conv-state-only-throwing-logger-goal";
        var sentinel = new InvalidOperationException("state-only-throwing-logger-sentinel");

        SeedPipelineWithConversation(goalId, ("user", "durable-entry"));
        SeedBlob(goalId, DurableBlob);

        var interceptor = new OneShotPipelinesDmlThrowInterceptor(sentinel);
        var context = CreateContext(interceptor);
        var logger = new ThrowingStoreLogger();
        var store = new PipelineStore(context, logger);
        logger.Arm();

        var principal = context.Pipelines.Find(goalId);
        Assert.NotNull(principal);

        var pipeline = NewPipeline(goalId);
        pipeline.SetActiveTask("rejected-throwing-logger-task");

        var thrown = Record.Exception(
            () => store.SavePipelineState(pipeline, pipeline.CaptureAdmissionOwnership()));

        Assert.NotNull(thrown);
        var update = Assert.IsType<DbUpdateException>(thrown);
        Assert.Same(sentinel, update.InnerException);
        Assert.Same(interceptor.SaveChangesFailure, update);
        Assert.DoesNotContain(EnumerateChain(thrown!), e => ReferenceEquals(e, logger.LoggerSentinel));

        // The reload really ran (the principal is Unchanged at database truth), so the guard's
        // silence did not skip the cleanup.
        context.ChangeTracker.DetectChanges();
        Assert.Equal(EntityState.Unchanged, context.Entry(principal!).State);
        Assert.Same(principal, context.Pipelines.Find(goalId));
    }

    /// <summary>
    /// THE DOUBLE-FAILURE BOUNDARY, on the REAL checkpoint cleanup: the cleanup's own reload SELECT
    /// is failed AND its targeted detach FALLBACK is failed too (the <c>StateChanges</c> event throws
    /// only when the TARGET <see cref="PipelineEntity"/> transitions to
    /// <see cref="EntityState.Detached"/>). Both cleanup steps are swallowed by their own guards, so
    /// the EXACT original row-write <see cref="DbUpdateException"/> still propagates by identity —
    /// never the reload or detach fault — and the SUSPECT diagnostic reports that the failed
    /// checkpoint's tracker hygiene did NOT complete, WITHOUT claiming preservation or safe reuse.
    /// No successful-cleanup or preservation diagnostic may appear anywhere on this path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE FAULT INJECTION uses EF's own public <c>ChangeTracker.StateChanged</c> event — the same
    /// mechanism the conversation-cleanup SUSPECT vector below uses — narrowed to a
    /// <see cref="PipelineEntity"/> so the fault lands ONLY on the fallback detach of the principal
    /// and can never misfire on a conversation dependent or on the preceding failed
    /// <c>SaveChanges</c> (whose pipeline transition is to Unchanged, not Detached). No production
    /// fault framework is added.
    /// </para>
    /// <para>
    /// THE MUTATIONS THIS KILLS: (1) skipping the fallback detach after a failed reload leaves the
    /// rejected checkpoint flushable — but that is the single-failure vector's job; THIS vector's
    /// mutants are (2) letting the detach fault replace the primary exception (identity assertion
    /// fails) and (3) emitting the COMPLETED or reload-failed diagnostic instead of the SUSPECT one
    /// on the double-failure path (the diagnostic assertions fail). The thrown-fault counter proves
    /// the detach really ran and really threw, so the vector is not vacuous.
    /// </para>
    /// <para>
    /// THE HONEST LIMITATION (asserted, not glossed over): when BOTH cleanup steps fail, the
    /// rejected tracking may still be staged — so this vector deliberately does NOT assert that a
    /// later save cannot flush the rejected blob, and it asserts the SUSPECT report instead.
    /// </para>
    /// </remarks>
    [Fact]
    public void StateOnlySave_OwnershipRoute_ReloadAndFallbackBothFail_ReportsSuspectAndPreservesOriginal()
    {
        const string goalId = "conv-state-only-double-fail-goal";
        var writeSentinel = new InvalidOperationException("state-only-double-fail-write-sentinel");
        var selectSentinel = new InvalidOperationException("state-only-double-fail-reload-sentinel");
        var detachSentinel = new InvalidOperationException("state-only-double-fail-detach-sentinel");

        SeedPipelineWithConversation(goalId, ("user", "durable-entry"));
        SeedBlob(goalId, DurableBlob);

        var select = new ArmablePipelinesSelectThrowInterceptor(selectSentinel);
        var write = new OneShotPipelinesDmlThrowInterceptor(writeSentinel);
        var context = CreateContext(write, select);
        var logger = new TestLogger<PipelineStore>();
        var store = new PipelineStore(context, logger);

        var principal = context.Pipelines.Find(goalId);
        Assert.NotNull(principal);

        // THE DOUBLE-FAILURE ARM: the reload SELECT fails (the armed interceptor) and the fallback
        // DETACH fails (this StateChanged subscriber, PipelineEntity-only so a conversation
        // dependent's detach — if any ran — could not carry the fault). The counter proves the
        // fallback detach really executed and really threw.
        var detachFaults = 0;
        context.ChangeTracker.StateChanged += (_, args) =>
        {
            if (args.NewState == EntityState.Detached && args.Entry.Entity is PipelineEntity)
            {
                Interlocked.Increment(ref detachFaults);
                throw detachSentinel;
            }
        };

        // The principal is ALREADY tracked, so the first armed `pipelines` SELECT is the cleanup's
        // reload; the separate one-shot interceptor fails the row write.
        select.Arm();

        var pipeline = NewPipeline(goalId);
        pipeline.SetActiveTask("rejected-double-fail-task");

        var thrown = Record.Exception(
            () => store.SavePipelineState(pipeline, pipeline.CaptureAdmissionOwnership()));

        // THE FAULTS REALLY LANDED: the row write failed, the reload SELECT failed, and the fallback
        // detach threw exactly once — the SUSPECT branch is reached for real, not vacuously.
        Assert.Equal(1, write.ThrowCount);
        Assert.Equal(1, select.ThrowCount);
        Assert.Equal(1, Volatile.Read(ref detachFaults));

        // THE PRIMARY EXCEPTION IS PRESERVED, BY IDENTITY — never the reload fault and never the
        // detach fault.
        Assert.NotNull(thrown);
        var update = Assert.IsType<DbUpdateException>(thrown);
        Assert.Same(writeSentinel, update.InnerException);
        Assert.Same(write.SaveChangesFailure, update);
        Assert.DoesNotContain(EnumerateChain(thrown!), e => ReferenceEquals(e, selectSentinel));
        Assert.DoesNotContain(EnumerateChain(thrown!), e => ReferenceEquals(e, detachSentinel));

        // …and the SUSPECT diagnostic really named the failed checkpoint cleanup, carrying the
        // DETACH fault's message (the fallback fault is the one that escalated this call).
        var suspect = Assert.Single(logger.LogEntries, e => e.Message.Contains(
            "ownership-checkpoint-cleanup", StringComparison.Ordinal));
        Assert.Contains("tracker hygiene did not complete", suspect.Message, StringComparison.Ordinal);
        Assert.Contains("SUSPECT", suspect.Message, StringComparison.Ordinal);
        Assert.Contains(goalId, suspect.Message, StringComparison.Ordinal);
        Assert.Contains("state-only-double-fail-detach-sentinel", suspect.Message, StringComparison.Ordinal);

        // NO successful-cleanup or preservation claim anywhere on this path.
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(
            "tracker hygiene completed", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(
            "the existing-row reload failed", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(
            "reload found NO durable row", StringComparison.Ordinal));

        // THE HONEST LIMITATION: with both cleanup steps faulted, the rejected tracking may still be
        // staged — this vector asserts the SUSPECT report, NOT a non-leakage guarantee.
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

    /// <summary>
    /// Throws the supplied sentinel at EVERY <c>SELECT … FROM "pipelines"</c> read ONCE ARMED — the
    /// seam for the CLEANUP-TIME reload failure. Arming is explicit and happens only after the
    /// principal is already tracked, so the interceptor cannot misfire on the pre-failure lookups
    /// that come first.
    /// </summary>
    private sealed class ArmablePipelinesSelectThrowInterceptor(Exception sentinel) : DbCommandInterceptor
    {
        private volatile bool _armed;
        private int _throwCount;

        public int ThrowCount => Volatile.Read(ref _throwCount);

        public void Arm() => _armed = true;

        public void Disarm() => _armed = false;

        private void ThrowIfTargeted(DbCommand command)
        {
            if (!_armed)
                return;

            var text = command.CommandText;
            if (!text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                return;
            if (!text.Contains("pipelines", StringComparison.OrdinalIgnoreCase))
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
