using System.Data.Common;
using System.Globalization;

using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Persistence;

/// <summary>
/// The worker-assignment-context STORE, end to end against a REAL file-backed SQLite database and
/// focused fakes: the INSERT-once write contract
/// (<c>Recorded</c>/<c>AlreadyRecorded</c>/<c>Conflict</c>/<c>Indeterminate</c>), the frozen context
/// values, the ordinal/value-wise duplicate comparison, the read-error versus write-uncertainty
/// separation, the guarded cleanup, the two-connection race, and the TEST-ONLY recoverability
/// demonstration.
/// </summary>
/// <remarks>
/// <para>
/// EVERY FIXTURE IS ISOLATED: the database is a per-instance temporary FILE and every context is
/// created from a connection STRING with <c>Pooling=False</c>, so EF owns and closes its own connection
/// and the file is deletable afterwards. A readback is therefore a genuine fresh open of the file,
/// never a read through a handle the test left dangling.
/// </para>
/// <para>
/// THE INJECTIONS ARE GENUINE EF INTERCEPTORS AND REAL DOMAIN FAILURES — a command interceptor that
/// throws a pre-created sentinel at a specific interception point, an affected-row-count override at
/// the provider boundary, a transaction interceptor that throws AFTER the underlying commit has landed,
/// a throwing logger, and a disposal seam that really disposes and then fails — never a fabricated
/// token. Raw assertions read the table through their own connection, bypassing EF's change tracker
/// entirely.
/// </para>
/// </remarks>
public sealed class WorkerAssignmentContextStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"copilothive-wac-store-{Guid.NewGuid():N}.db");

    private readonly List<IDisposable> _fixtures = [];

    /// <summary>The one instant every fixed-clock fixture records, so the stored text is predictable.</summary>
    private static readonly DateTimeOffset FixedNow =
        new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero).AddTicks(1234567);

    private static readonly string FixedNowText =
        FixedNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public WorkerAssignmentContextStoreTests()
    {
        // A REAL database FILE with the full current schema.
        using var connection = OpenConnection();
        using var context = ContextOn(connection);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (var fixture in _fixtures)
        {
            try
            {
                fixture.Dispose();
            }
            catch
            {
                // Best-effort — a leftover fixture must never fail a test.
            }
        }

        SqliteConnection.ClearAllPools();
        foreach (var candidate in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
            catch
            {
                // Best-effort cleanup — a leftover temp file must never fail a test.
            }
        }
    }

    // ───────────────────────────── fixture plumbing ─────────────────────────────

    /// <summary>A finite SQLite busy timeout so a contended writer retries instead of failing fast.</summary>
    private string ConnectionString => $"Data Source={_dbPath};Pooling=False;Default Timeout=15";

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private static CopilotHiveDbContext ContextOn(SqliteConnection connection, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);
        return new CopilotHiveDbContext(builder.Options);
    }

    private AssignmentContextFactory NewFactory(params IInterceptor[] interceptors)
    {
        var factory = new AssignmentContextFactory(ConnectionString, interceptors);
        _fixtures.Add(factory);
        return factory;
    }

    private static WorkerAssignmentContextStore NewStore(
        IDbContextFactory<CopilotHiveDbContext> factory,
        TimeProvider? timeProvider = null,
        ILogger<WorkerAssignmentContextStore>? logger = null) =>
        new(factory, logger ?? NullLogger<WorkerAssignmentContextStore>.Instance, timeProvider);

    private void ExecuteRaw(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? RawScalar(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>Seeds one row of the assignment-context table with RAW column text (corruption vectors).</summary>
    private void SeedRawAssignmentRow(
        string taskId,
        string goalId = "goal-seeded",
        string workerId = "worker-seeded",
        string role = "coder",
        string phase = "coding",
        string model = "model-seeded",
        string iteration = "1",
        string occurrence = "1",
        string attempt = "1",
        string firstAssignedAtUtc = "2020-01-02T03:04:05.0000000Z") =>
        ExecuteRaw(
            "INSERT INTO worker_assignment_contexts " +
            "(task_id, goal_id, worker_id, role, phase, model, iteration, occurrence, attempt, first_assigned_at_utc) " +
            $"VALUES ('{Escape(taskId)}', '{Escape(goalId)}', '{Escape(workerId)}', '{Escape(role)}', " +
            $"'{Escape(phase)}', '{Escape(model)}', {iteration}, {occurrence}, {attempt}, '{Escape(firstAssignedAtUtc)}')");

    private long AssignmentRowCount(string taskId) =>
        (long)RawScalar($"SELECT COUNT(*) FROM worker_assignment_contexts WHERE task_id = '{Escape(taskId)}'")!;

    private string? RawColumn(string taskId, string column) =>
        (string?)RawScalar(
            $"SELECT {column} FROM worker_assignment_contexts WHERE task_id = '{Escape(taskId)}'");

    /// <summary>Every column of a row of the named table, keyed by column name — the byte-identity baseline.</summary>
    private Dictionary<string, object?> ReadWholeRow(string table, string keyColumn, string keyValue)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} WHERE {keyColumn} = '{Escape(keyValue)}'";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"no {table} row for {keyColumn} '{keyValue}'");

        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return row;
    }

    // ───────────────────────────── context builders ─────────────────────────────

    private static WorkerAssignmentContext Context(
        string goalId = "goal-base",
        string workerId = "worker-base",
        string taskId = "task-base",
        GoalPhase phase = GoalPhase.Coding,
        WorkerRole role = WorkerRole.Coder,
        int iteration = 1,
        int occurrence = 1,
        int attempt = 1,
        string model = "model-base") =>
        new(goalId, workerId, role, new WorkSlot(taskId, new WorkSlotPosition(iteration, phase, occurrence), attempt), model);

    /// <summary>
    /// The representative context shapes the round-trip matrix covers: the legacy task id form, a
    /// SUFFIXED task id form, every worker-backed phase/role pair, an empty model, a whitespace model,
    /// a provider-prefixed model, and multi-digit position numbers. The task ids prove the id is
    /// carried OPAQUELY — nothing parses or rewrites them.
    /// </summary>
    public static TheoryData<string> ContextVariants => new()
    {
        "legacy-task-id",
        "suffixed-task-id",
        "testing-phase",
        "review-phase",
        "docwriting-phase",
        "improve-phase",
        "empty-model",
        "whitespace-model",
        "provider-prefixed-model",
        "multi-digit-position",
    };

    private static WorkerAssignmentContext Variant(string kind, string taskId) => kind switch
    {
        "legacy-task-id" => Context(taskId: taskId),
        "suffixed-task-id" => Context(taskId: taskId, attempt: 3, occurrence: 2),
        "testing-phase" => Context(taskId: taskId, phase: GoalPhase.Testing, role: WorkerRole.Tester),
        "review-phase" => Context(taskId: taskId, phase: GoalPhase.Review, role: WorkerRole.Reviewer),
        "docwriting-phase" => Context(taskId: taskId, phase: GoalPhase.DocWriting, role: WorkerRole.DocWriter),
        "improve-phase" => Context(taskId: taskId, phase: GoalPhase.Improve, role: WorkerRole.Improver),
        "empty-model" => Context(taskId: taskId, model: ""),
        "whitespace-model" => Context(taskId: taskId, model: "  \t "),
        "provider-prefixed-model" => Context(taskId: taskId, model: "copilot/claude-sonnet-4.6"),
        "multi-digit-position" => Context(taskId: taskId, iteration: 7, occurrence: 3, attempt: 12),
        _ => throw new InvalidOperationException($"Unknown context variant '{kind}'."),
    };

    // ═══════════════════════════════════════════════════════════════════════
    // (1) Round trip across a genuine close-and-reopen of the database file
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FULL ROUND TRIP, for every representative context shape: record through the store, dispose
    /// the factory, then load through a FRESH factory over the same file. EVERY context field survives —
    /// goal, worker, role, the whole slot identity and the verbatim model — together with the EXACT
    /// first-assigned UTC ticks and stored text, and the RAW column texts prove the shared storage
    /// conventions (lowercase enums, invariant "O" timestamp).
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: drop any member from the INSERT's parameter list (or its column) and the
    /// corresponding field assertion fails; substitute a defaulted/rounded timestamp and both the ticks
    /// assertion and the raw-text assertion fail; remove the lowercase enum conversion from the store's
    /// parameter binding and the raw role/phase text assertions fail.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ContextVariants))]
    public void InsertOnce_ThenFreshFactoryLoad_ReproducesEveryContextFieldAndExactFirstAssignedTime(string kind)
    {
        // TWO OPAQUE TASK IDS: the legacy form and a new SUFFIXED form, both accepted verbatim.
        var taskId = kind == "suffixed-task-id" ? "task-suffixed-attempt-2" : $"task-variant-{kind}";
        var context = Variant(kind, taskId);
        Assert.Equal(taskId, context.Slot.TaskId);

        var writerFactory = NewFactory();
        var writer = NewStore(writerFactory, new AssignmentClock(FixedNow));

        var writeResult = writer.InsertOnce(context);

        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, writeResult.Status);
        Assert.Null(writeResult.WriteException);

        // THE RAW ROW really exists, and the typed columns carry the shared storage conventions.
        Assert.Equal(1L, AssignmentRowCount(taskId));
        Assert.Equal(taskId, RawColumn(taskId, "task_id"));
        Assert.Equal(context.GoalId, RawColumn(taskId, "goal_id"));
        Assert.Equal(context.WorkerId, RawColumn(taskId, "worker_id"));
        Assert.Equal(context.Role.ToString().ToLowerInvariant(), RawColumn(taskId, "role"));
        Assert.Equal(context.Slot.Position.Phase.ToString().ToLowerInvariant(), RawColumn(taskId, "phase"));
        Assert.Equal(context.Model, RawColumn(taskId, "model"));
        Assert.Equal(context.Slot.Position.Iteration.ToString(CultureInfo.InvariantCulture),
            ScalarText(RawScalar($"SELECT iteration FROM worker_assignment_contexts WHERE task_id = '{Escape(taskId)}'")));
        Assert.Equal(FixedNowText, RawColumn(taskId, "first_assigned_at_utc"));

        // DISPOSE THE WRITER FIXTURE before reading anything back.
        writerFactory.Dispose();

        var readerFactory = NewFactory();
        var reader = NewStore(readerFactory, new AssignmentClock(FixedNow));

        var loaded = reader.Load(taskId);

        Assert.NotNull(loaded);
        // EVERY CONTEXT FIELD.
        Assert.Equal(context.GoalId, loaded!.Context.GoalId);
        Assert.Equal(context.WorkerId, loaded.Context.WorkerId);
        Assert.Equal(context.Role, loaded.Context.Role);
        Assert.Equal(context.Slot.TaskId, loaded.Context.Slot.TaskId);
        Assert.Equal(context.Slot.Position.Iteration, loaded.Context.Slot.Position.Iteration);
        Assert.Equal(context.Slot.Position.Phase, loaded.Context.Slot.Position.Phase);
        Assert.Equal(context.Slot.Position.Occurrence, loaded.Context.Slot.Position.Occurrence);
        Assert.Equal(context.Slot.Attempt, loaded.Context.Slot.Attempt);
        Assert.Equal(context.Model, loaded.Context.Model);
        Assert.Equal(context, loaded.Context); // the whole value, member for member

        // THE EXACT FIRST-ASSIGNED INSTANT: ticks, kind and the stored text all agree.
        Assert.Equal(FixedNow.UtcDateTime.Ticks, loaded.FirstAssignedAtUtc.Ticks);
        Assert.Equal(DateTimeKind.Utc, loaded.FirstAssignedAtUtc.Kind);
        Assert.Equal(FixedNow.UtcDateTime, loaded.FirstAssignedAtUtc);
        Assert.Equal(FixedNowText, loaded.FirstAssignedAtUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    private static string? ScalarText(object? value) =>
        value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// TWO LOADS RETURN TWO INDEPENDENT INSTANCES of an IMMUTABLE value: they are equal by value but
    /// never the same reference, so a caller can never mutate a stored row through a loaded context.
    /// </summary>
    [Fact]
    public void Load_Twice_ReturnsEqualButDistinctImmutableContexts()
    {
        var context = Context(taskId: "task-detach");
        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, store.InsertOnce(context).Status);

        var first = store.Load("task-detach");
        var second = store.Load("task-detach");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.NotSame(first!.Context, second!.Context);
        Assert.Equal(first.Context, second.Context);

        // The loaded context exposes NO writable property — nothing to mutate.
        Assert.DoesNotContain(typeof(WorkerAssignmentContext).GetProperties(), p => p.CanWrite);

        // …and the RAW row is untouched after both loads.
        Assert.Equal(1L, AssignmentRowCount("task-detach"));
    }

    /// <summary>
    /// TASK IDS ARE OPAQUE: a legacy id and a suffixed id (which a parsing implementation might have
    /// treated as the SAME slot) are TWO DISTINCT recorded assignments, each stored VERBATIM with its
    /// own context and its own row.
    /// </summary>
    [Fact]
    public void InsertOnce_OpaqueTaskIds_LegacyAndSuffixedForms_AreRecordedVerbatim()
    {
        const string legacy = "task-opaque";
        const string suffixed = "task-opaque-attempt-2";

        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        var legacyContext = Context(taskId: legacy, attempt: 1);
        var suffixedContext = Context(taskId: suffixed, attempt: 2);

        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, store.InsertOnce(legacyContext).Status);
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, store.InsertOnce(suffixedContext).Status);

        // TWO rows, both ids preserved byte-for-byte (nothing stripped, parsed or normalized).
        Assert.Equal(1L, AssignmentRowCount(legacy));
        Assert.Equal(1L, AssignmentRowCount(suffixed));
        Assert.Equal(legacy, RawColumn(legacy, "task_id"));
        Assert.Equal(suffixed, RawColumn(suffixed, "task_id"));
        Assert.Equal(2L, (long)RawScalar("SELECT COUNT(*) FROM worker_assignment_contexts")!);
        Assert.Equal(1, store.Load(legacy)!.Context.Slot.Attempt);
        Assert.Equal(2, store.Load(suffixed)!.Context.Slot.Attempt);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) The duplicate and conflict truths
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AN IDENTICAL RETRY: the second insert matches the first row's every context value, so it is
    /// <see cref="WorkerAssignmentWriteStatus.AlreadyRecorded"/> — and the original row and its
    /// first-assigned time are untouched (the retry's later clock is NOT written).
    /// </summary>
    [Fact]
    public void InsertOnce_IdenticalRepeat_AlreadyRecorded_OriginalRowAndTimeUnchanged()
    {
        const string taskId = "task-identical";
        var context = Context(taskId: taskId, model: "copilot/claude-sonnet-4.6");
        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        var first = store.InsertOnce(context);
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, first.Status);
        Assert.Equal(1L, AssignmentRowCount(taskId));

        // A LATER clock for the retry: if the retry refreshed the timestamp, the text would move.
        var retryStore = NewStore(factory, new AssignmentClock(FixedNow.AddHours(5)));
        var second = retryStore.InsertOnce(Context(taskId: taskId, model: "copilot/claude-sonnet-4.6"));

        Assert.Equal(WorkerAssignmentWriteStatus.AlreadyRecorded, second.Status);
        Assert.Null(second.WriteException);
        Assert.Equal(1L, AssignmentRowCount(taskId));
        Assert.Equal(FixedNowText, RawColumn(taskId, "first_assigned_at_utc"));

        // THE SAME INSTANCE-INDEPENDENT VALUE: the retry's own (later) instant was never stored.
        Assert.NotEqual(
            FixedNow.AddHours(5).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            RawColumn(taskId, "first_assigned_at_utc"));
    }

    /// <summary>
    /// THE EQUIVALENT-BUT-NON-CANONICAL STORED ROW: the row holds the same context with a DIFFERENTLY
    /// CASED stored role/phase text and a different recording instant. The zero-row insert is followed
    /// by a successful materialization and a VALUE-WISE comparison of the CONTEXT (the timestamp is not
    /// part of it), so the outcome is <see cref="WorkerAssignmentWriteStatus.AlreadyRecorded"/> — and
    /// the stored bytes are left EXACTLY as seeded.
    /// </summary>
    /// <remarks>
    /// The stored enum texts really are non-canonical, so the EF converter's case-insensitive read side
    /// is what makes the row materialize at all; the timestamp deliberately differs from the retry's
    /// clock, proving the time is excluded from the duplicate decision and never refreshed.
    /// </remarks>
    [Fact]
    public void InsertOnce_EquivalentStoredRowWithDifferentCaseAndTime_AlreadyRecorded_RawBytesUntouched()
    {
        const string taskId = "task-noncanonical";
        const string seededTime = "2020-01-02T03:04:05.0000000Z";
        var context = Context(taskId: taskId, phase: GoalPhase.DocWriting, role: WorkerRole.DocWriter);
        SeedRawAssignmentRow(
            taskId, goalId: context.GoalId, workerId: context.WorkerId,
            role: "DOCWRITER", phase: "DocWriting", model: context.Model, firstAssignedAtUtc: seededTime);

        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        var result = store.InsertOnce(context);

        Assert.Equal(WorkerAssignmentWriteStatus.AlreadyRecorded, result.Status);
        Assert.Null(result.WriteException);
        // BYTE-FOR-BYTE UNCHANGED — the equivalent row was NOT rewritten into canonical form.
        Assert.Equal("DOCWRITER", RawColumn(taskId, "role"));
        Assert.Equal("DocWriting", RawColumn(taskId, "phase"));
        Assert.Equal(seededTime, RawColumn(taskId, "first_assigned_at_utc"));
        Assert.Equal(1L, AssignmentRowCount(taskId));

        // …and it still loads, with the materialized enum values and the SEEDED instant preserved.
        var loaded = store.Load(taskId);
        Assert.NotNull(loaded);
        Assert.Equal(WorkerRole.DocWriter, loaded!.Context.Role);
        Assert.Equal(GoalPhase.DocWriting, loaded.Context.Slot.Position.Phase);
        Assert.Equal(DateTime.ParseExact(seededTime, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            loaded.FirstAssignedAtUtc);
    }

    /// <summary>
    /// THE CONFLICT MATRIX: a zero-row insert followed by a VALID but DIFFERENT row is
    /// <see cref="WorkerAssignmentWriteStatus.Conflict"/> — including a different goal, worker, model,
    /// slot position or attempt, and each of the other worker-backed phase/role pairs. Nothing changes:
    /// the stored values and the first-assigned time stay byte-identical, and no second row appears.
    /// </summary>
    /// <remarks>
    /// Each vector asserts its own precondition (the candidate context really differs from the stored
    /// one), so a vector that accidentally rebuilt the base context cannot pass as a conflict. The
    /// case-only vectors pin the comparison as ORDINAL: a case-INSENSITIVE comparison would wrongly
    /// report <c>AlreadyRecorded</c>.
    /// </remarks>
    [Theory]
    [InlineData("goal")]
    [InlineData("case-only-goal")]
    [InlineData("worker")]
    [InlineData("case-only-worker")]
    [InlineData("model")]
    [InlineData("case-only-model")]
    [InlineData("iteration")]
    [InlineData("occurrence")]
    [InlineData("attempt")]
    [InlineData("phase-role")]
    [InlineData("testing-phase")]
    [InlineData("improve-phase")]
    public void InsertOnce_DifferingContext_Conflict_OriginalRowAndTimeUnchanged(string kind)
    {
        const string taskId = "task-conflict";
        var stored = Context(taskId: taskId, model: "model-base");
        var candidate = ConflictingCandidate(kind, taskId);
        Assert.NotEqual(stored, candidate); // the vector really differs

        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, store.InsertOnce(stored).Status);

        var retryStore = NewStore(factory, new AssignmentClock(FixedNow.AddDays(1)));
        var result = retryStore.InsertOnce(candidate);

        Assert.Equal(WorkerAssignmentWriteStatus.Conflict, result.Status);
        Assert.Null(result.WriteException);
        Assert.Equal(1L, AssignmentRowCount(taskId));
        Assert.Equal(FixedNowText, RawColumn(taskId, "first_assigned_at_utc"));

        // The stored context is STILL the original one — never overwritten or rebound.
        var loaded = store.Load(taskId);
        Assert.NotNull(loaded);
        Assert.Equal(stored, loaded!.Context);
        Assert.Equal(FixedNowText, loaded.FirstAssignedAtUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    private static WorkerAssignmentContext ConflictingCandidate(string kind, string taskId) => kind switch
    {
        "goal" => Context(goalId: "goal-other", taskId: taskId),
        // THE CASE-ONLY VECTORS: only letter case differs, so an ordinal-IGNORE-CASE comparison would
        // falsely report AlreadyRecorded. A genuine conflict must be decided ordinally.
        "case-only-goal" => Context(goalId: "GOAL-BASE", taskId: taskId),
        "worker" => Context(workerId: "worker-other", taskId: taskId),
        "case-only-worker" => Context(workerId: "WORKER-BASE", taskId: taskId),
        "model" => Context(taskId: taskId, model: "model-other"),
        "case-only-model" => Context(taskId: taskId, model: "MODEL-BASE"),
        "iteration" => Context(taskId: taskId, iteration: 2),
        "occurrence" => Context(taskId: taskId, occurrence: 2),
        "attempt" => Context(taskId: taskId, attempt: 2),
        "phase-role" => Context(taskId: taskId, phase: GoalPhase.Review, role: WorkerRole.Reviewer),
        "testing-phase" => Context(taskId: taskId, phase: GoalPhase.Testing, role: WorkerRole.Tester),
        "improve-phase" => Context(taskId: taskId, phase: GoalPhase.Improve, role: WorkerRole.Improver),
        _ => throw new InvalidOperationException($"Unknown conflict vector '{kind}'."),
    };

    // ═══════════════════════════════════════════════════════════════════════
    // (3) Absence, and read/integrity errors that must THROW
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>ABSENCE IS THE ONLY NULL: a missing row loads as <c>null</c>, and nothing is written.</summary>
    [Fact]
    public void Load_MissingRow_ReturnsNull_AndWritesNothing()
    {
        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        Assert.Null(store.Load("task-absent"));
        Assert.Equal(0L, (long)RawScalar("SELECT COUNT(*) FROM worker_assignment_contexts")!);
    }

    /// <summary>BLANK OR NULL TASK IDENTITIES are refused before a context exists — no row, no statement.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Load_BlankTaskId_ThrowsArgumentException_AndAcquiresNothing(string blank)
    {
        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        var ex = Assert.Throws<ArgumentException>(() => store.Load(blank));
        Assert.Equal("taskId", ex.ParamName);
        Assert.Equal(0, factory.CreateCount);
    }

    /// <summary>NULL IDENTITY is the null-argument refusal, also before a context exists.</summary>
    [Fact]
    public void Load_NullTaskId_ThrowsArgumentNull_AndAcquiresNothing()
    {
        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        var ex = Assert.Throws<ArgumentNullException>(() => store.Load(null!));
        Assert.Equal("taskId", ex.ParamName);
        Assert.Equal(0, factory.CreateCount);
    }

    /// <summary>
    /// A STORED ROW THAT VIOLATES THE CONTEXT CONTRACT is CORRUPTION and THROWS on both paths —
    /// <c>Load</c> and the zero-row duplicate path — as a read/integrity error, never as
    /// <c>Conflict</c>/<c>AlreadyRecorded</c>/<c>Indeterminate</c> and never as an absence. No role,
    /// position or model is guessed or repaired.
    /// </summary>
    [Theory]
    [InlineData("non-worker-phase", "orchestrator", "planning", "1", "1", "1", "goal-base", "worker-base")]
    [InlineData("role-phase-mismatch", "coder", "testing", "1", "1", "1", "goal-base", "worker-base")]
    [InlineData("non-positive-iteration", "coder", "coding", "0", "1", "1", "goal-base", "worker-base")]
    [InlineData("non-positive-occurrence", "coder", "coding", "1", "0", "1", "goal-base", "worker-base")]
    [InlineData("non-positive-attempt", "coder", "coding", "1", "1", "0", "goal-base", "worker-base")]
    [InlineData("blank-goal", "coder", "coding", "1", "1", "1", "   ", "worker-base")]
    [InlineData("blank-worker", "coder", "coding", "1", "1", "1", "goal-base", "   ")]
    public void StoredRowViolatingContextContract_LoadAndDuplicatePath_ThrowInvalidOperation(
        string label, string role, string phase, string iteration, string occurrence, string attempt,
        string goalId, string workerId)
    {
        var taskId = $"task-corrupt-{label}";
        SeedRawAssignmentRow(
            taskId, goalId: goalId, workerId: workerId, role: role, phase: phase,
            iteration: iteration, occurrence: occurrence, attempt: attempt);

        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        // LOAD THROWS — corruption is never reported as an absence.
        var loadError = Assert.Throws<InvalidOperationException>(() => store.Load(taskId));
        Assert.Contains("not a valid context", loadError.Message, StringComparison.Ordinal);
        Assert.NotNull(loadError.InnerException);

        // THE DUPLICATE PATH THROWS TOO — it is NOT a Conflict and NOT an AlreadyRecorded.
        var candidate = Context(goalId: "goal-base", workerId: "worker-base", taskId: taskId);
        var duplicateError = Assert.Throws<InvalidOperationException>(() => store.InsertOnce(candidate));
        Assert.Contains("not a valid context", duplicateError.Message, StringComparison.Ordinal);

        // The corrupt row was left exactly as seeded — nothing repaired, nothing overwritten.
        Assert.Equal(1L, AssignmentRowCount(taskId));
        Assert.Equal(role, RawColumn(taskId, "role"));
    }

    /// <summary>
    /// UNPARSEABLE STORED TEXT is a read error and THROWS: an unknown role/phase label fails the enum
    /// value converter, and an unmaterializable first-assigned column fails the timestamp converter.
    /// Neither is silently defaulted.
    /// </summary>
    [Theory]
    [InlineData("role", "bogus-role", "coding", "2020-01-02T03:04:05.0000000Z")]
    [InlineData("phase", "coder", "bogus-phase", "2020-01-02T03:04:05.0000000Z")]
    [InlineData("timestamp", "coder", "coding", "not-a-timestamp")]
    public void Load_UnparseableStoredText_Throws(string label, string role, string phase, string timestamp)
    {
        var taskId = $"task-unparseable-{label}";
        SeedRawAssignmentRow(taskId, role: role, phase: phase, firstAssignedAtUtc: timestamp);

        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        var thrown = Record.Exception(() => store.Load(taskId));

        Assert.NotNull(thrown);
        Assert.False(string.IsNullOrWhiteSpace(thrown!.Message));

        // The corrupt row is still exactly as seeded after the failed read.
        Assert.Equal(1L, AssignmentRowCount(taskId));
    }

    /// <summary>
    /// THE VANISHED-ROW BRANCH: a confirmed ZERO-ROW insert whose row is then NOT there. The zero count
    /// is produced by genuinely SUPPRESSING the provider's execution at the EF interception point, so the
    /// statement reports zero affected rows AND no row exists — exactly the inconsistent state the
    /// missing-row guard exists for. It is an explicit <see cref="InvalidOperationException"/> naming THE
    /// SPECIFIC TASK, never <c>Indeterminate</c>, never <c>Recorded</c>, never <c>AlreadyRecorded</c> or
    /// <c>Conflict</c>, and never a silent absence.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: demote the missing-row <c>throw</c> to an <c>Indeterminate</c> result (or to a
    /// <c>Conflict</c>/<c>AlreadyRecorded</c>, or to returning <c>null</c>) and <c>Assert.Throws</c>
    /// fails outright; weaken the message so it no longer names the task or the zero-row insert and the
    /// message assertions fail; let the branch fall through to a successful outcome and the
    /// "no result was produced" assertion fails. The <c>SuppressCount</c> assertion proves the injection
    /// really fired, so the test cannot pass vacuously by never reaching the branch, and the fresh
    /// readback proves the zero count and the absent row genuinely agree.
    /// </remarks>
    [Fact]
    public void InsertOnce_ZeroRowsButRowMissing_ThrowsInvalidOperationNamingTheTask_NotIndeterminateOrRecorded()
    {
        const string taskId = "task-vanished";
        var context = Context(goalId: "goal-vanished", workerId: "worker-vanished", taskId: taskId);

        // PRECONDITION (anti-vacuous): the row genuinely does not exist before the attempt.
        Assert.Equal(0L, AssignmentRowCount(taskId));

        var interceptor = new AssignmentInsertSuppressingInterceptor();
        var factory = NewFactory(interceptor);
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        // NO RESULT IS PRODUCED AT ALL — the branch THROWS rather than reporting any write outcome.
        WorkerAssignmentWriteResult? produced = null;
        var thrown = Assert.Throws<InvalidOperationException>(() => produced = store.InsertOnce(context));

        Assert.Null(produced);

        // THE INJECTION REALLY FIRED: the ON CONFLICT/zero-row path was genuinely taken.
        Assert.Equal(1, interceptor.SuppressCount);

        // THE MESSAGE IS TASK-SPECIFIC and names the zero-row insert that produced the inconsistency.
        Assert.Contains(taskId, thrown.Message, StringComparison.Ordinal);
        Assert.Contains("is missing after a zero-row insert", thrown.Message, StringComparison.Ordinal);
        // It is NOT the corrupt-context message and NOT an unexpected-count message: this is its own
        // distinct integrity failure.
        Assert.DoesNotContain("not a valid context", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("expected exactly 0 or 1", thrown.Message, StringComparison.Ordinal);

        // A FRESH READBACK CONFIRMS THE ABSENCE: the zero count and the missing row really do agree, so
        // the guard fired on a genuine inconsistency and not on a row it simply failed to see.
        Assert.Equal(0L, AssignmentRowCount(taskId));
        Assert.Equal(0L, (long)RawScalar("SELECT COUNT(*) FROM worker_assignment_contexts")!);

        // …and a fresh factory/store (a genuine reopen) also sees nothing.
        var readerFactory = NewFactory();
        var reader = NewStore(readerFactory, new AssignmentClock(FixedNow));
        Assert.Null(reader.Load(taskId));

        // AN EXPLICIT RETRY, with the suppression gone, settles to Recorded — the store performed no
        // hidden reconciliation and left nothing behind.
        var retryFactory = NewFactory();
        var retryStore = NewStore(retryFactory, new AssignmentClock(FixedNow));
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, retryStore.InsertOnce(context).Status);
        Assert.Equal(1L, AssignmentRowCount(taskId));
    }

    /// <summary>
    /// A FAILING READ QUERY stays a THROW: the exact interceptor sentinel propagates out of both
    /// <c>Load</c> and the zero-row duplicate path, so a read fault is never dressed up as write
    /// uncertainty.
    /// </summary>
    [Fact]
    public void ReadQueryFailure_LoadAndDuplicatePath_PropagateExactException_NotWriteFailure()
    {
        const string taskId = "task-read-throw";
        var context = Context(taskId: taskId);
        SeedRawAssignmentRow(taskId, goalId: context.GoalId, workerId: context.WorkerId,
            model: context.Model);

        var interceptor = new AssignmentSelectThrowingInterceptor();
        var factory = NewFactory(interceptor);
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        var loadThrown = Assert.Throws<InvalidOperationException>(() => store.Load(taskId));
        Assert.Same(interceptor.Sentinel, loadThrown);

        // The zero-row duplicate path issues its own SELECT and hits the same fault: it THROWS rather
        // than returning Indeterminate with a fabricated WriteException.
        var insertThrown = Assert.Throws<InvalidOperationException>(() => store.InsertOnce(context));
        Assert.Same(interceptor.Sentinel, insertThrown);
        Assert.Equal(2, interceptor.ThrowCount);

        // The row is still exactly as seeded — the failed duplicate attempt changed nothing.
        Assert.Equal(1L, AssignmentRowCount(taskId));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (4) The write shape, pinned: one parameterized INSERT, one explicit transaction
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE WRITE SHAPE, PINNED: EXACTLY ONE statement touching the table — the narrow parameterized
    /// <c>INSERT … ON CONFLICT(task_id) DO NOTHING</c> with all TEN values bound as parameters — no
    /// SELECT/reader/scalar command and no UPDATE/DELETE against the table, ONE explicit transaction
    /// begun, and no literal value anywhere in the SQL.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: add a pre-read, change the statement to an UPDATE/REPLACE/upsert, inline any
    /// value into the SQL text, drop a bound parameter, or drop the explicit transaction, and one of
    /// the assertions below fails.
    /// </remarks>
    [Fact]
    public void InsertOnce_Recorded_IssuesExactlyOneParameterizedInsertInOneExplicitTransaction()
    {
        const string taskId = "task-shape";
        var context = Context(
            goalId: "goal-shape", workerId: "worker-shape", taskId: taskId,
            phase: GoalPhase.Improve, role: WorkerRole.Improver,
            iteration: 2, occurrence: 3, attempt: 4, model: "copilot/claude-sonnet-4.6");

        var capture = new AssignmentCommandCaptureInterceptor();
        var transactions = new AssignmentTransactionCountInterceptor();
        var factory = NewFactory(capture, transactions);
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        var result = store.InsertOnce(context);

        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, result.Status);

        var commands = capture.Commands.Where(c =>
            c.Sql.Contains("worker_assignment_contexts", StringComparison.OrdinalIgnoreCase)).ToList();

        var insert = Assert.Single(commands, c => c.Kind == "NonQuery"
            && c.Sql.TrimStart().StartsWith("INSERT INTO worker_assignment_contexts", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ON CONFLICT(task_id) DO NOTHING", insert.Sql, StringComparison.Ordinal);

        // ALL TEN VALUES TRAVELLED AS PARAMETERS.
        Assert.Equal(
            new[]
            {
                "@attempt", "@firstAssignedAtUtc", "@goalId", "@iteration", "@model",
                "@occurrence", "@phase", "@role", "@taskId", "@workerId",
            },
            insert.Parameters.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(taskId, insert.Parameters["@taskId"]);
        Assert.Equal("goal-shape", insert.Parameters["@goalId"]);
        Assert.Equal("worker-shape", insert.Parameters["@workerId"]);
        Assert.Equal("improver", insert.Parameters["@role"]);
        Assert.Equal("improve", insert.Parameters["@phase"]);
        Assert.Equal("copilot/claude-sonnet-4.6", insert.Parameters["@model"]);
        Assert.Equal(2, insert.Parameters["@iteration"]);
        Assert.Equal(3, insert.Parameters["@occurrence"]);
        Assert.Equal(4, insert.Parameters["@attempt"]);
        Assert.Equal(FixedNowText, insert.Parameters["@firstAssignedAtUtc"]);

        // NO PRE-READ, NO UPDATE, NO DELETE — against the table, in any statement kind.
        Assert.DoesNotContain(commands, c => c.Kind is "Reader" or "Scalar");
        Assert.DoesNotContain(commands, c => c.Sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(commands, c => c.Sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(commands, c => c.Sql.Contains("DELETE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(commands, c => c.Sql.Contains("REPLACE", StringComparison.OrdinalIgnoreCase));

        // THE SQL CARRIES NO LITERAL VALUES — every value travelled as a parameter.
        foreach (var command in commands)
        {
            Assert.DoesNotContain(taskId, command.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("goal-shape", command.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("worker-shape", command.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("copilot/claude-sonnet-4.6", command.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain(FixedNowText, command.Sql, StringComparison.Ordinal);
        }

        // EXACTLY ONE EXPLICIT TRANSACTION, and no automatic (SaveChanges-scoped) one.
        Assert.Equal(1, transactions.StartCount);
        Assert.Equal(1, transactions.CommitCount);
        Assert.Equal(0, transactions.RollbackCount);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (5) Write uncertainty — before execution, after the commit landed, and unexpected counts
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A FAILURE BEFORE EXECUTION (<c>NonQueryExecuting</c>): the INSERT never ran, so the outcome is
    /// <see cref="WorkerAssignmentWriteStatus.Indeterminate"/> carrying the EXACT sentinel, the fresh
    /// readback shows NO row, and an explicit retry settles to <c>Recorded</c>.
    /// </summary>
    [Fact]
    public void InsertOnce_FailureBeforeExecution_IndeterminateExactException_AbsentRow_RetrySettlesRecorded()
    {
        const string taskId = "task-before-exec";
        var context = Context(taskId: taskId);
        var interceptor = new AssignmentInsertThrowingInterceptor(AssignmentInsertFault.BeforeExecution);
        var factory = NewFactory(interceptor);
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        var result = store.InsertOnce(context);

        Assert.Equal(WorkerAssignmentWriteStatus.Indeterminate, result.Status);
        Assert.Same(interceptor.Sentinel, result.WriteException);
        Assert.Equal(1, interceptor.FireCount);

        // THE READBACK DISTINGUISHES THE REALITY: the statement never ran, so the row is ABSENT.
        Assert.Equal(0L, AssignmentRowCount(taskId));

        // AN EXPLICIT RETRY SETTLES CORRECTLY.
        interceptor.Disarm();
        var retry = store.InsertOnce(context);
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, retry.Status);
        Assert.Null(retry.WriteException);
        Assert.Equal(1L, AssignmentRowCount(taskId));
    }

    /// <summary>
    /// A FAILURE AFTER THE UNDERLYING COMMIT LANDED (a throw raised once the transaction has already
    /// committed): the row IS durable even though the call threw, so the outcome is still
    /// <see cref="WorkerAssignmentWriteStatus.Indeterminate"/> carrying the EXACT sentinel — and the
    /// fresh readback proves the row is PRESENT and complete. An explicit retry then settles to
    /// <c>AlreadyRecorded</c> against the very row the failed call wrote, with its time unchanged.
    /// </summary>
    /// <remarks>
    /// THIS IS THE "AFTER AUTOCOMMIT" VECTOR for a store that owns its transaction: the fault is
    /// injected on the transaction's committed notification, so it fires only once the durable write
    /// has already happened — the case where a rollback claim would be a lie.
    /// </remarks>
    [Fact]
    public void InsertOnce_FailureAfterCommitLanded_IndeterminateExactException_PresentRow_RetrySettlesAlreadyRecorded()
    {
        const string taskId = "task-after-commit";
        var context = Context(taskId: taskId);
        var interceptor = new AssignmentCommitThrowingInterceptor();
        var factory = NewFactory(interceptor);
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        var result = store.InsertOnce(context);

        Assert.Equal(WorkerAssignmentWriteStatus.Indeterminate, result.Status);
        Assert.Same(interceptor.Sentinel, result.WriteException);
        Assert.Equal(1, interceptor.FireCount);

        // THE READBACK DISTINGUISHES THE REALITY: the commit landed, so the row is PRESENT and complete
        // — the store made no claim about it, and this is the caller's evidence.
        Assert.Equal(1L, AssignmentRowCount(taskId));
        Assert.Equal(context.GoalId, RawColumn(taskId, "goal_id"));
        Assert.Equal(context.WorkerId, RawColumn(taskId, "worker_id"));
        Assert.Equal(context.Model, RawColumn(taskId, "model"));
        Assert.Equal(FixedNowText, RawColumn(taskId, "first_assigned_at_utc"));

        // AN EXPLICIT RETRY SETTLES AGAINST THE ROW THE FAILED CALL WROTE.
        interceptor.Disarm();
        var retryStore = NewStore(factory, new AssignmentClock(FixedNow.AddMinutes(1)));
        var retry = retryStore.InsertOnce(context);
        Assert.Equal(WorkerAssignmentWriteStatus.AlreadyRecorded, retry.Status);
        Assert.Equal(FixedNowText, RawColumn(taskId, "first_assigned_at_utc")); // the time did NOT move
    }

    /// <summary>
    /// A FAILURE AFTER THE STATEMENT EXECUTED BUT BEFORE THE COMMIT: the INSERT really ran inside a
    /// transaction that was never committed, so the outcome is
    /// <see cref="WorkerAssignmentWriteStatus.Indeterminate"/> with the EXACT sentinel and the fresh
    /// readback shows the row ABSENT — the honest truth, because only the committed state is durable.
    /// </summary>
    [Fact]
    public void InsertOnce_FailureAfterExecutionBeforeCommit_IndeterminateExactException_RowNotDurable()
    {
        const string taskId = "task-after-exec";
        var context = Context(taskId: taskId);
        var interceptor = new AssignmentInsertThrowingInterceptor(AssignmentInsertFault.AfterExecution);
        var factory = NewFactory(interceptor);
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        var result = store.InsertOnce(context);

        Assert.Equal(WorkerAssignmentWriteStatus.Indeterminate, result.Status);
        Assert.Same(interceptor.Sentinel, result.WriteException);
        Assert.Equal(1, interceptor.FireCount);

        // THE READBACK IS AUTHORITATIVE: the uncommitted transaction was discarded, so no row exists.
        Assert.Equal(0L, AssignmentRowCount(taskId));

        // AN EXPLICIT RETRY SETTLES CORRECTLY.
        interceptor.Disarm();
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, store.InsertOnce(context).Status);
        Assert.Equal(1L, AssignmentRowCount(taskId));
    }

    /// <summary>
    /// AN UNEXPECTED AFFECTED-ROW COUNT IS NEVER A SUCCESS AND NEVER A DUPLICATE: <c>-1</c> (the
    /// provider's "unknown" count) and <c>2</c> both yield
    /// <see cref="WorkerAssignmentWriteStatus.Indeterminate"/> carrying an
    /// <see cref="InvalidOperationException"/> that NAMES the count, and the readback shows what the
    /// discarded transaction really left behind.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void InsertOnce_UnexpectedAffectedRowCount_IndeterminateWithCountException_NeverSuccess(int forcedCount)
    {
        var taskId = $"task-count-{forcedCount}";
        var context = Context(taskId: taskId);
        var interceptor = new AssignmentRowCountInterceptor(forcedCount);
        var factory = NewFactory(interceptor);
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        var result = store.InsertOnce(context);

        Assert.Equal(WorkerAssignmentWriteStatus.Indeterminate, result.Status);
        Assert.Equal(1, interceptor.OverrideCount);
        var error = Assert.IsType<InvalidOperationException>(result.WriteException);
        Assert.Contains(forcedCount.ToString(CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.Contains("expected exactly 0 or 1", error.Message, StringComparison.Ordinal);

        // THE WRITE WAS NEVER REPORTED AS A SUCCESS, and the readback shows the truth: the transaction
        // was discarded, so no row exists.
        Assert.Equal(0L, AssignmentRowCount(taskId));

        // An explicit retry settles correctly.
        interceptor.Disarm();
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, store.InsertOnce(context).Status);
        Assert.Equal(1L, AssignmentRowCount(taskId));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (6) Pre-write refusals — nothing acquired, nothing written
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EVERY INVALID INPUT IS REFUSED BY THE CONTEXT'S OWN CONSTRUCTOR — before any store call exists,
    /// so no factory context is ever acquired. Non-blank identities, non-null slot/position/model,
    /// positive iteration/occurrence/attempt, worker-backed phases and the mapped role are all enforced;
    /// the refusals name the offending member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ONE GUARD PER VECTOR. Each row below carries EXACTLY ONE fault and every other value is valid, so
    /// the refusal it observes can only have come from the guard it targets — a vector can never be
    /// killed by an unrelated rule firing first.
    /// </para>
    /// <para>
    /// REMOVAL-PROOF, per guard: each vector asserts the offending parameter name AND a guard-specific
    /// message fragment, so weakening a single rule leaves exactly that vector failing. In particular:
    /// the WHITESPACE identity vectors kill a null-only identity check (<c>goalId is null</c> instead of
    /// <c>IsNullOrWhiteSpace</c>), and the NEGATIVE counter vectors kill a <c>!= 0</c> check written in
    /// place of <c>&lt;= 0</c> — in both cases the weakened guard stops throwing and
    /// <c>Assert.Throws</c> fails. The zero vectors remain alongside them so a rule narrowed the other
    /// way (rejecting only negatives) is caught too.
    /// </para>
    /// </remarks>
    [Theory]
    // ── identities: null AND whitespace, each in its own vector ──
    [InlineData("null-goal", null, "worker", "task", "coding", "coder", 1, 1, 1, "goalId", "goal ID must be a non-blank string")]
    [InlineData("whitespace-goal", "   ", "worker", "task", "coding", "coder", 1, 1, 1, "goalId", "goal ID must be a non-blank string")]
    [InlineData("tab-goal", "\t", "worker", "task", "coding", "coder", 1, 1, 1, "goalId", "goal ID must be a non-blank string")]
    [InlineData("empty-goal", "", "worker", "task", "coding", "coder", 1, 1, 1, "goalId", "goal ID must be a non-blank string")]
    [InlineData("null-worker", "goal", null, "task", "coding", "coder", 1, 1, 1, "workerId", "worker ID must be a non-blank string")]
    [InlineData("whitespace-worker", "goal", "  ", "task", "coding", "coder", 1, 1, 1, "workerId", "worker ID must be a non-blank string")]
    [InlineData("empty-worker", "goal", "", "task", "coding", "coder", 1, 1, 1, "workerId", "worker ID must be a non-blank string")]
    [InlineData("null-task", "goal", "worker", null, "coding", "coder", 1, 1, 1, "slot", "slot task ID must be a non-blank string")]
    [InlineData("whitespace-task", "goal", "worker", "  ", "coding", "coder", 1, 1, 1, "slot", "slot task ID must be a non-blank string")]
    [InlineData("empty-task", "goal", "worker", "", "coding", "coder", 1, 1, 1, "slot", "slot task ID must be a non-blank string")]
    // ── counters: zero AND negative, each in its own vector, each naming its own field and value ──
    [InlineData("zero-iteration", "goal", "worker", "task", "coding", "coder", 0, 1, 1, "slot", "iteration must be positive but was 0")]
    [InlineData("negative-iteration", "goal", "worker", "task", "coding", "coder", -3, 1, 1, "slot", "iteration must be positive but was -3")]
    [InlineData("zero-occurrence", "goal", "worker", "task", "coding", "coder", 1, 0, 1, "slot", "occurrence must be positive but was 0")]
    [InlineData("negative-occurrence", "goal", "worker", "task", "coding", "coder", 1, -2, 1, "slot", "occurrence must be positive but was -2")]
    [InlineData("zero-attempt", "goal", "worker", "task", "coding", "coder", 1, 1, 0, "slot", "attempt must be positive but was 0")]
    [InlineData("negative-attempt", "goal", "worker", "task", "coding", "coder", 1, 1, -5, "slot", "attempt must be positive but was -5")]
    // ── the non-worker phases and the role/phase mapping ──
    [InlineData("planning-phase", "goal", "worker", "task", "planning", "coder", 1, 1, 1, "slot", "phase 'Planning' has no worker")]
    [InlineData("merging-phase", "goal", "worker", "task", "merging", "mergeworker", 1, 1, 1, "slot", "phase 'Merging' has no worker")]
    [InlineData("done-phase", "goal", "worker", "task", "done", "orchestrator", 1, 1, 1, "slot", "phase 'Done' has no worker")]
    [InlineData("failed-phase", "goal", "worker", "task", "failed", "coder", 1, 1, 1, "slot", "phase 'Failed' has no worker")]
    [InlineData("role-phase-mismatch", "goal", "worker", "task", "coding", "tester", 1, 1, 1, "role", "role 'Tester' does not match the role 'Coder'")]
    [InlineData("phase-role-mismatch", "goal", "worker", "task", "testing", "coder", 1, 1, 1, "role", "role 'Coder' does not match the role 'Tester'")]
    [InlineData("unspecified-role", "goal", "worker", "task", "coding", "unspecified", 1, 1, 1, "role", "role 'Unspecified' does not match the role 'Coder'")]
    [InlineData("orchestrator-role", "goal", "worker", "task", "coding", "orchestrator", 1, 1, 1, "role", "role 'Orchestrator' does not match the role 'Coder'")]
    [InlineData("mergeworker-role", "goal", "worker", "task", "coding", "mergeworker", 1, 1, 1, "role", "role 'MergeWorker' does not match the role 'Coder'")]
    public void InvalidInput_FailsBeforeContextAcquisition(
        string label, string? goalId, string? workerId, string? taskId, string phaseName,
        string roleName, int iteration, int occurrence, int attempt, string expectedParamName,
        string expectedMessageFragment)
    {
        var factory = NewFactory();

        var ex = Assert.Throws<ArgumentException>(() =>
            BuildContext(goalId, workerId, taskId, phaseName, roleName, iteration, occurrence, attempt));

        // THE GUARD-SPECIFIC EVIDENCE: the offending parameter AND the rule that fired. A different rule
        // firing first (or a weakened one letting the value through) cannot satisfy both.
        Assert.Equal(expectedParamName, ex.ParamName);
        Assert.Contains(expectedMessageFragment, ex.Message, StringComparison.Ordinal);

        // NOTHING WAS ACQUIRED AND NOTHING WAS WRITTEN — and the store was never even reached.
        Assert.True(0 == factory.CreateCount, $"vector '{label}' acquired a context before validation.");
        Assert.True(0L == (long)RawScalar("SELECT COUNT(*) FROM worker_assignment_contexts")!,
            $"vector '{label}' wrote a row despite being invalid.");
    }

    /// <summary>
    /// THE SAME ISOLATED INVALID VECTORS, DRIVEN THROUGH THE STORE: a caller that hands
    /// <see cref="WorkerAssignmentContextStore.InsertOnce"/> an invalid candidate cannot even build one,
    /// so the refusal happens before the store acquires a context and no row is ever written. This pins
    /// the "validation fails BEFORE any context acquisition" contract at the store boundary rather than
    /// only at the value type.
    /// </summary>
    [Theory]
    [InlineData("whitespace-goal", "   ", "worker", "task", 1, 1, 1)]
    [InlineData("whitespace-worker", "goal", "  ", "task", 1, 1, 1)]
    [InlineData("whitespace-task", "goal", "worker", " \t ", 1, 1, 1)]
    [InlineData("negative-occurrence", "goal", "worker", "task", 1, -2, 1)]
    [InlineData("negative-attempt", "goal", "worker", "task", 1, 1, -5)]
    public void InvalidInput_ThroughStore_FailsBeforeContextAcquisition_AndPersistsNothing(
        string label, string goalId, string workerId, string taskId, int iteration, int occurrence, int attempt)
    {
        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        // The candidate cannot be constructed at all, so InsertOnce is never reached with a bad value.
        Assert.Throws<ArgumentException>(() => store.InsertOnce(
            BuildContext(goalId, workerId, taskId, "coding", "coder", iteration, occurrence, attempt)));

        Assert.True(0 == factory.CreateCount, $"vector '{label}' acquired a context before validation.");
        Assert.True(0L == (long)RawScalar("SELECT COUNT(*) FROM worker_assignment_contexts")!,
            $"vector '{label}' wrote a row despite being invalid.");
    }

    /// <summary>
    /// AN UNDEFINED PHASE VALUE (an out-of-range enum cast) is refused by the same contract, before any
    /// context acquisition.
    /// </summary>
    [Fact]
    public void InvalidInput_UndefinedPhaseAndRoleValues_FailBeforeContextAcquisition()
    {
        var factory = NewFactory();

        Assert.Throws<ArgumentException>(() => BuildContext(
            "goal", "worker", "task", (GoalPhase)999, WorkerRole.Coder, 1, 1, 1));
        Assert.Throws<ArgumentException>(() => BuildContext(
            "goal", "worker", "task", GoalPhase.Coding, (WorkerRole)999, 1, 1, 1));

        Assert.Equal(0, factory.CreateCount);
    }

    /// <summary>
    /// A NULL SLOT, a SLOT WITHOUT A POSITION and a NULL MODEL are refused by the null/shape rules —
    /// again before any context acquisition.
    /// </summary>
    [Fact]
    public void InvalidInput_NullSlot_NullPosition_AndNullModel_FailBeforeContextAcquisition()
    {
        var factory = NewFactory();

        var slotError = Assert.Throws<ArgumentNullException>(() =>
            new WorkerAssignmentContext("goal", "worker", WorkerRole.Coder, null!, "model"));
        Assert.Equal("slot", slotError.ParamName);

        var positionError = Assert.Throws<ArgumentException>(() =>
            new WorkerAssignmentContext(
                "goal", "worker", WorkerRole.Coder, new WorkSlot("task", null!, 1), "model"));
        Assert.Equal("slot", positionError.ParamName);

        var modelError = Assert.Throws<ArgumentNullException>(() =>
            new WorkerAssignmentContext(
                "goal", "worker", WorkerRole.Coder, new WorkSlot("task", new WorkSlotPosition(1, GoalPhase.Coding, 1), 1), null!));
        Assert.Equal("model", modelError.ParamName);

        Assert.Equal(0, factory.CreateCount);
    }

    /// <summary>A NULL CANDIDATE is refused before a context exists.</summary>
    [Fact]
    public void InsertOnce_NullContext_ThrowsArgumentNull_AndAcquiresNothing()
    {
        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        var ex = Assert.Throws<ArgumentNullException>(() => store.InsertOnce(null!));
        Assert.Equal("context", ex.ParamName);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0L, (long)RawScalar("SELECT COUNT(*) FROM worker_assignment_contexts")!);
    }

    /// <summary>
    /// THE RESULT PAIRING IS STRUCTURAL, not merely documented: the four factories are the ONLY way to
    /// obtain a <see cref="WorkerAssignmentWriteResult"/>, and each produces the documented pairing —
    /// evidence for <c>Indeterminate</c> alone, and no evidence for the three confirmed outcomes. A
    /// caller cannot fabricate an inconsistent pair because no accessible constructor exists.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: re-expose the positional/public constructor (or drop the validating guard inside
    /// it) and the "no accessible two-argument constructor" assertion fails; swap a factory's evidence
    /// (e.g. hand <c>Recorded</c> an exception, or <c>Indeterminate</c> a null) and the pairing
    /// assertions fail.
    /// </remarks>
    [Fact]
    public void WriteResult_FactoriesAreTheOnlyConstructionPath_AndEnforceTheDocumentedPairing()
    {
        // THE THREE CONFIRMED OUTCOMES CARRY NO EVIDENCE.
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, WorkerAssignmentWriteResult.Recorded().Status);
        Assert.Null(WorkerAssignmentWriteResult.Recorded().WriteException);
        Assert.Equal(WorkerAssignmentWriteStatus.AlreadyRecorded, WorkerAssignmentWriteResult.AlreadyRecorded().Status);
        Assert.Null(WorkerAssignmentWriteResult.AlreadyRecorded().WriteException);
        Assert.Equal(WorkerAssignmentWriteStatus.Conflict, WorkerAssignmentWriteResult.Conflict().Status);
        Assert.Null(WorkerAssignmentWriteResult.Conflict().WriteException);

        // THE UNRESOLVED OUTCOME CARRIES THE EXACT EVIDENCE OBJECT.
        var sentinel = new InvalidOperationException("write sentinel");
        var indeterminate = WorkerAssignmentWriteResult.Indeterminate(sentinel);
        Assert.Equal(WorkerAssignmentWriteStatus.Indeterminate, indeterminate.Status);
        Assert.Same(sentinel, indeterminate.WriteException);

        // NO ACCESSIBLE CONSTRUCTOR: the pairing cannot be bypassed from anywhere in the assembly, so an
        // inconsistent result (a confirmed outcome with evidence, or Indeterminate without) is
        // unconstructible rather than merely discouraged. The sealed record's compiler-generated copy
        // constructor is itself private, so it is no escape hatch either.
        var constructors = typeof(WorkerAssignmentWriteResult)
            .GetConstructors(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance);
        Assert.DoesNotContain(constructors, c => !c.IsPrivate);
    }

    /// <summary>
    /// THE EMPTY AND WHITESPACE MODELS ARE VALID AND PRESERVED VERBATIM — no provider normalization, no
    /// trimming and no default is ever substituted.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData("  copilot/claude-sonnet-4.6  ")]
    [InlineData("Provider/Vendor/Model-With-Weird Case")]
    public void InsertOnce_EmptyWhitespaceOrPrefixedModel_IsStoredVerbatim(string model)
    {
        var taskId = $"task-model-{model.Length}-{model.GetHashCode(StringComparison.Ordinal):X8}";
        var context = Context(taskId: taskId, model: model);

        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));

        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, store.InsertOnce(context).Status);
        Assert.Equal(model, RawColumn(taskId, "model"));
        Assert.Equal(model, store.Load(taskId)!.Context.Model);
    }

    /// <summary>
    /// A CLOCK FAILURE propagates the EXACT exception and happens BEFORE any context is acquired or any
    /// statement is issued.
    /// </summary>
    [Fact]
    public void InsertOnce_ClockThrows_PropagatesExactException_AndAcquiresNothing()
    {
        var factory = NewFactory();
        var sentinel = new InvalidOperationException("clock sentinel");
        var store = NewStore(factory, new AssignmentClockThrows(sentinel));

        var thrown = Assert.Throws<InvalidOperationException>(() => store.InsertOnce(Context(taskId: "task-clock")));

        Assert.Same(sentinel, thrown);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0L, (long)RawScalar("SELECT COUNT(*) FROM worker_assignment_contexts")!);
    }

    /// <summary>
    /// A CONTEXT-ACQUISITION FAILURE propagates the EXACT exception with nothing to clean up: the
    /// disposal seam is never invoked because no context was ever acquired.
    /// </summary>
    [Fact]
    public void InsertOnce_ContextAcquisitionThrows_PropagatesExactException_AndDisposesNothing()
    {
        var sentinel = new InvalidOperationException("acquisition sentinel");
        var store = NewStore(new AssignmentThrowingContextFactory(sentinel), new AssignmentClock(FixedNow));
        var disposeCalls = 0;
        store.ContextDisposerForTest = _ =>
        {
            disposeCalls++;
            throw new InvalidOperationException("cleanup must not run without an acquisition");
        };

        var thrown = Assert.Throws<InvalidOperationException>(
            () => store.InsertOnce(Context(taskId: "task-acquire")));

        Assert.Same(sentinel, thrown);
        Assert.Equal(0, disposeCalls);
    }

    private static WorkerAssignmentContext BuildContext(
        string? goalId, string? workerId, string? taskId, string phaseName, string roleName,
        int iteration, int occurrence, int attempt) =>
        new(
            goalId!,
            workerId!,
            Enum.Parse<WorkerRole>(roleName, true),
            new WorkSlot(taskId!, new WorkSlotPosition(iteration, Enum.Parse<GoalPhase>(phaseName, true), occurrence), attempt),
            "model");

    private static WorkerAssignmentContext BuildContext(
        string goalId, string workerId, string taskId, GoalPhase phase, WorkerRole role,
        int iteration, int occurrence, int attempt) =>
        new(goalId, workerId, role, new WorkSlot(taskId, new WorkSlotPosition(iteration, phase, occurrence), attempt), "model");

    // ═══════════════════════════════════════════════════════════════════════
    // (7) The guarded cleanup — never masked
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A FAILING CONTEXT DISPOSAL PRESERVES THE CONFIRMED RESULTS: <c>Recorded</c>,
    /// <c>AlreadyRecorded</c> and <c>Conflict</c> are all returned unchanged (with a
    /// <c>context-dispose</c> warning logged), and the disposal failure's own sentinel never escapes.
    /// The injected disposer really disposes the context first, so no resource leaks.
    /// </summary>
    [Theory]
    [InlineData("recorded")]
    [InlineData("already-recorded")]
    [InlineData("conflict")]
    public void InsertOnce_DisposalFails_ConfirmedResultsPreserved(string scenario)
    {
        var taskId = $"task-dispose-{scenario}";
        var stored = Context(taskId: taskId);
        if (scenario != "recorded")
            SeedRawAssignmentRow(taskId, goalId: stored.GoalId, workerId: stored.WorkerId, model: stored.Model);

        var logger = new TestLogger<WorkerAssignmentContextStore>();
        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow), logger);
        var sentinel = new InvalidOperationException("dispose sentinel");
        var disposeCalls = 0;
        store.ContextDisposerForTest = context =>
        {
            disposeCalls++;
            context.Dispose(); // the REAL release still happens
            throw sentinel;
        };

        var candidate = scenario == "conflict" ? Context(taskId: taskId, workerId: "worker-other") : stored;

        var result = store.InsertOnce(candidate);

        var expected = scenario switch
        {
            "recorded" => WorkerAssignmentWriteStatus.Recorded,
            "already-recorded" => WorkerAssignmentWriteStatus.AlreadyRecorded,
            _ => WorkerAssignmentWriteStatus.Conflict,
        };
        Assert.Equal(expected, result.Status);
        Assert.Null(result.WriteException);
        Assert.Equal(1, disposeCalls);

        // The guarded warning carries the identifiers and the disposal failure's own message — and the
        // failure NEVER escapes as an exception (the result above is what the caller sees).
        var warning = Assert.Single(logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
        Assert.Contains("context-dispose", warning.Message, StringComparison.Ordinal);
        Assert.Contains(taskId, warning.Message, StringComparison.Ordinal);
        Assert.Contains("dispose sentinel", warning.Message, StringComparison.Ordinal);
        Assert.Null(warning.Exception);
    }

    /// <summary>
    /// A FAILING DISPOSAL PRESERVES WRITE UNCERTAINTY TOO: the <c>Indeterminate</c> outcome and its
    /// EXACT <see cref="WorkerAssignmentWriteResult.WriteException"/> survive a throwing disposer.
    /// </summary>
    [Fact]
    public void InsertOnce_DisposalFails_IndeterminateEvidencePreserved()
    {
        const string taskId = "task-dispose-indeterminate";
        var context = Context(taskId: taskId);
        var writeFault = new AssignmentInsertThrowingInterceptor(AssignmentInsertFault.BeforeExecution);
        var logger = new TestLogger<WorkerAssignmentContextStore>();
        var factory = NewFactory(writeFault);
        var store = NewStore(factory, new AssignmentClock(FixedNow), logger);
        store.ContextDisposerForTest = owned =>
        {
            owned.Dispose();
            throw new InvalidOperationException("dispose sentinel");
        };

        var result = store.InsertOnce(context);

        Assert.Equal(WorkerAssignmentWriteStatus.Indeterminate, result.Status);
        Assert.Same(writeFault.Sentinel, result.WriteException);
        Assert.Contains(logger.LogEntries, e => e.LogLevel == LogLevel.Warning
            && e.Message.Contains("context-dispose", StringComparison.Ordinal));
    }

    /// <summary>
    /// A FAILING DISPOSAL PRESERVES A PROPAGATING READ ERROR: the store's own integrity exception still
    /// escapes (with the disposal warning logged) — the cleanup never replaces it.
    /// </summary>
    [Fact]
    public void Load_DisposalFails_PropagatingReadErrorPreserved()
    {
        const string taskId = "task-dispose-read-error";
        SeedRawAssignmentRow(taskId, role: "coder", phase: "testing"); // role/phase mismatch: corrupt

        var logger = new TestLogger<WorkerAssignmentContextStore>();
        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow), logger);
        store.ContextDisposerForTest = owned =>
        {
            owned.Dispose();
            throw new InvalidOperationException("dispose sentinel");
        };

        var thrown = Assert.Throws<InvalidOperationException>(() => store.Load(taskId));

        Assert.Contains("not a valid context", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(logger.LogEntries, e => e.LogLevel == LogLevel.Warning
            && e.Message.Contains("context-dispose", StringComparison.Ordinal));
    }

    /// <summary>
    /// A CLEANUP EXCEPTION WHOSE <c>Message</c> GETTER ITSELF THROWS cannot escape the finally: the
    /// whole diagnostic (message access included) sits inside the no-throw guard, so the confirmed
    /// result stays authoritative.
    /// </summary>
    [Fact]
    public void InsertOnce_DisposalThrowsUnreadableMessage_ConfirmedResultStillReturned()
    {
        const string taskId = "task-unreadable-cleanup";
        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));
        store.ContextDisposerForTest = owned =>
        {
            owned.Dispose();
            throw new ThrowingMessageException("the disposal failure is unreadable");
        };

        var result = store.InsertOnce(Context(taskId: taskId));

        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, result.Status);
        Assert.Null(result.WriteException);
        Assert.Equal(1L, AssignmentRowCount(taskId));
    }

    /// <summary>
    /// A THROWING LOGGER CANNOT MASK ANYTHING: with the logger armed to throw on every write, a disposal
    /// failure still leaves the confirmed result intact, the uncertainty evidence intact, and the
    /// propagating read error intact.
    /// </summary>
    [Fact]
    public void InsertOnce_ThrowingLogger_NeverMasksResultOrException()
    {
        // (a) A CONFIRMED RESULT with a failing disposal and a throwing logger.
        var loggerA = new ThrowingLogger<WorkerAssignmentContextStore>();
        var factoryA = NewFactory();
        var storeA = NewStore(factoryA, new AssignmentClock(FixedNow), loggerA);
        loggerA.Arm();
        storeA.ContextDisposerForTest = owned =>
        {
            owned.Dispose();
            throw new InvalidOperationException("dispose sentinel");
        };

        var recorded = storeA.InsertOnce(Context(taskId: "task-throwing-logger"));
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, recorded.Status);
        Assert.Null(recorded.WriteException);
        Assert.True(loggerA.ThrowCount >= 1, "the guarded warning never reached the throwing logger");

        // (b) WRITE UNCERTAINTY with a throwing logger: the EXACT evidence still comes back.
        var loggerB = new ThrowingLogger<WorkerAssignmentContextStore>();
        var writeFault = new AssignmentInsertThrowingInterceptor(AssignmentInsertFault.BeforeExecution);
        var factoryB = NewFactory(writeFault);
        var storeB = NewStore(factoryB, new AssignmentClock(FixedNow), loggerB);
        loggerB.Arm();

        var uncertain = storeB.InsertOnce(Context(taskId: "task-throwing-logger-uncertain"));
        Assert.Equal(WorkerAssignmentWriteStatus.Indeterminate, uncertain.Status);
        Assert.Same(writeFault.Sentinel, uncertain.WriteException);
        Assert.True(loggerB.ThrowCount >= 1);

        // (c) A PROPAGATING READ ERROR with a throwing logger.
        const string readTaskId = "task-throwing-logger-read";
        SeedRawAssignmentRow(readTaskId, role: "coder", phase: "review");
        var loggerC = new ThrowingLogger<WorkerAssignmentContextStore>();
        var factoryC = NewFactory();
        var storeC = NewStore(factoryC, new AssignmentClock(FixedNow), loggerC);
        loggerC.Arm();

        Assert.Throws<InvalidOperationException>(() => storeC.Load(readTaskId));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (8) The two-connection race — the database primary key arbitrates
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// TWO INDEPENDENT FACTORIES/CONNECTIONS, GATED AT THE INSERT: both producers reach the statement
    /// boundary before either executes, so the race is real and NOT ordered by sleeping. Identical
    /// candidates settle as exactly one <c>Recorded</c> plus one <c>AlreadyRecorded</c>, and the single
    /// surviving row is one complete winner.
    /// </summary>
    [Fact]
    public void ConcurrentIdenticalInserts_AcrossTwoConnections_ProduceRecordedAndAlreadyRecorded()
    {
        const string taskId = "task-race-identical";
        var context = Context(taskId: taskId, model: "copilot/claude-sonnet-4.6");

        using var gate = new AssignmentInsertGateInterceptor(participants: 2);
        var factoryA = NewFactory(gate);
        var factoryB = NewFactory(gate);
        var storeA = NewStore(factoryA, new AssignmentClock(FixedNow));
        var storeB = NewStore(factoryB, new AssignmentClock(FixedNow.AddSeconds(1)));

        var results = RunRace(
            () => storeA.InsertOnce(context),
            () => storeB.InsertOnce(context));

        Assert.Equal(2, gate.ArrivalCount);
        Assert.Equal(1, results.Count(r => r.Status == WorkerAssignmentWriteStatus.Recorded));
        Assert.Equal(1, results.Count(r => r.Status == WorkerAssignmentWriteStatus.AlreadyRecorded));
        Assert.All(results, r => Assert.Null(r.WriteException));

        // EXACTLY ONE COMPLETE WINNER ROW.
        Assert.Equal(1L, AssignmentRowCount(taskId));
        Assert.Equal(context.GoalId, RawColumn(taskId, "goal_id"));
        Assert.Equal(context.WorkerId, RawColumn(taskId, "worker_id"));
        Assert.Equal(context.Model, RawColumn(taskId, "model"));
        var storedText = RawColumn(taskId, "first_assigned_at_utc");
        Assert.Equal(1, new[]
        {
            FixedNowText,
            FixedNow.AddSeconds(1).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        }.Count(candidate => candidate == storedText));
    }

    /// <summary>
    /// THE DIFFERING-CANDIDATE RACE: two independent connections gated at the INSERT, with candidates
    /// that share the task id but differ in every other respect. The outcome is exactly one
    /// <c>Recorded</c> plus one <c>Conflict</c>, and the surviving row is ONE COMPLETE winner — its
    /// values are the winner's and its first-assigned time is the winner's clock, with no partial write
    /// and no rebound row.
    /// </summary>
    [Fact]
    public void ConcurrentDifferingInserts_AcrossTwoConnections_ProduceRecordedAndConflict_WithOneCompleteWinner()
    {
        const string taskId = "task-race-differing";
        var candidateA = Context(
            goalId: "goal-race-a", workerId: "worker-race-a", taskId: taskId,
            phase: GoalPhase.Coding, role: WorkerRole.Coder, model: "model-race-a");
        var candidateB = Context(
            goalId: "goal-race-b", workerId: "worker-race-b", taskId: taskId,
            phase: GoalPhase.Testing, role: WorkerRole.Tester, model: "model-race-b");

        using var gate = new AssignmentInsertGateInterceptor(participants: 2);
        var factoryA = NewFactory(gate);
        var factoryB = NewFactory(gate);
        var storeA = NewStore(factoryA, new AssignmentClock(FixedNow));
        var storeB = NewStore(factoryB, new AssignmentClock(FixedNow.AddSeconds(1)));

        var results = RunRace(
            () => storeA.InsertOnce(candidateA),
            () => storeB.InsertOnce(candidateB));

        Assert.Equal(2, gate.ArrivalCount);
        Assert.Equal(1, results.Count(r => r.Status == WorkerAssignmentWriteStatus.Recorded));
        Assert.Equal(1, results.Count(r => r.Status == WorkerAssignmentWriteStatus.Conflict));
        Assert.All(results, r => Assert.Null(r.WriteException));

        // EXACTLY ONE COMPLETE WINNER: every column matches whichever producer actually inserted.
        Assert.Equal(1L, AssignmentRowCount(taskId));
        var winnerIsA = results[0].Status == WorkerAssignmentWriteStatus.Recorded;
        Assert.Equal(winnerIsA ? "goal-race-a" : "goal-race-b", RawColumn(taskId, "goal_id"));
        Assert.Equal(winnerIsA ? "worker-race-a" : "worker-race-b", RawColumn(taskId, "worker_id"));
        Assert.Equal(winnerIsA ? "coder" : "tester", RawColumn(taskId, "role"));
        Assert.Equal(winnerIsA ? "coding" : "testing", RawColumn(taskId, "phase"));
        Assert.Equal(winnerIsA ? "model-race-a" : "model-race-b", RawColumn(taskId, "model"));
        Assert.Equal(winnerIsA ? FixedNowText
            : FixedNow.AddSeconds(1).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            RawColumn(taskId, "first_assigned_at_utc"));
    }

    /// <summary>
    /// Runs two producers on DEDICATED threads that both start from the same rendezvous, and joins both
    /// with a BOUNDED wait. All tasks are drained in <c>finally</c>, so a hang is a failure rather than a
    /// leak. No sleep-based ordering is used anywhere.
    /// </summary>
    private static List<WorkerAssignmentWriteResult> RunRace(
        Func<WorkerAssignmentWriteResult> first,
        Func<WorkerAssignmentWriteResult> second)
    {
        var results = new WorkerAssignmentWriteResult?[2];
        var failures = new Exception?[2];
        using var start = new Barrier(2);
        var threads = new Thread[2];

        for (var i = 0; i < 2; i++)
        {
            var index = i;
            var body = index == 0 ? first : second;
            threads[index] = new Thread(() =>
            {
                try
                {
                    start.SignalAndWait(RaceTimeout);
                    results[index] = body();
                }
                catch (Exception ex)
                {
                    failures[index] = ex;
                }
            })
            {
                IsBackground = true,
                Name = $"assignment-context-race-{index}",
            };
        }

        try
        {
            foreach (var thread in threads)
                thread.Start();

            foreach (var thread in threads)
                Assert.True(thread.Join(RaceTimeout), "A worker-assignment-context race producer never finished.");

            foreach (var failure in failures)
                Assert.Null(failure);

            return [.. results.Select(r => r ?? throw new InvalidOperationException("A producer produced no result."))];
        }
        finally
        {
            // DRAIN: every thread is bounded-joined, and any that somehow outlived the join is abandoned
            // as a background thread only after the assertions above have run.
            foreach (var thread in threads)
            {
                if (thread.IsAlive)
                    thread.Join(TimeSpan.FromSeconds(5));
            }
        }
    }

    /// <summary>Generous bound for a whole race — a hang is a failure, not a slow test.</summary>
    private static readonly TimeSpan RaceTimeout = TimeSpan.FromSeconds(60);

    // ═══════════════════════════════════════════════════════════════════════
    // (9) Isolation — assignment operations touch ONLY worker_assignment_contexts
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE BLAST RADIUS IS ONE TABLE: a seeded pipeline row, task-mapping row, conversation row and
    /// completion-receipt row are byte-identical before and after a full assignment workload (a recorded
    /// insert, a load, an identical duplicate and a conflict), and no extra rows appear anywhere.
    /// </summary>
    [Fact]
    public void AssignmentOperations_LeaveSeededPipelineMappingConversationAndReceiptRowsUnchanged()
    {
        ExecuteRaw(
            """
            INSERT INTO pipelines (goal_id, description, goal_json, phase, metrics_json, active_task_id, created_at)
            VALUES ('goal-seeded', 'Seeded', '{"id":"goal-seeded","description":"seeded","repositories":["r"]}',
                    'Coding', '{}', 'task-seeded', '2026-01-01T00:00:00.0000000Z')
            """);
        ExecuteRaw("INSERT INTO task_mappings (task_id, goal_id) VALUES ('task-seeded', 'goal-seeded')");
        ExecuteRaw("INSERT INTO conversation_entries (goal_id, seq, role, content) VALUES ('goal-seeded', 0, 'user', 'hello')");
        ExecuteRaw(
            """
            INSERT INTO completion_receipts (task_id, goal_id, payload_json, first_stored_at_utc)
            VALUES ('task-seeded', 'goal-seeded', '{"v":1}', '2026-01-01T00:00:00.0000000Z')
            """);

        var pipelineBefore = ReadWholeRow("pipelines", "goal_id", "goal-seeded");
        var mappingBefore = ReadWholeRow("task_mappings", "task_id", "task-seeded");
        var conversationBefore = ReadWholeRow("conversation_entries", "goal_id", "goal-seeded");
        var receiptBefore = ReadWholeRow("completion_receipts", "task_id", "task-seeded");

        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));
        var context = Context(goalId: "goal-seeded", workerId: "worker-seeded", taskId: "task-seeded");

        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, store.InsertOnce(context).Status);
        Assert.NotNull(store.Load("task-seeded"));
        Assert.Equal(WorkerAssignmentWriteStatus.AlreadyRecorded, store.InsertOnce(context).Status);
        Assert.Equal(
            WorkerAssignmentWriteStatus.Conflict,
            store.InsertOnce(Context(goalId: "goal-seeded", workerId: "worker-seeded", taskId: "task-seeded", model: "other")).Status);

        Assert.Equal(pipelineBefore, ReadWholeRow("pipelines", "goal_id", "goal-seeded"));
        Assert.Equal(mappingBefore, ReadWholeRow("task_mappings", "task_id", "task-seeded"));
        Assert.Equal(conversationBefore, ReadWholeRow("conversation_entries", "goal_id", "goal-seeded"));
        Assert.Equal(receiptBefore, ReadWholeRow("completion_receipts", "task_id", "task-seeded"));
        Assert.Equal(1L, (long)RawScalar("SELECT COUNT(*) FROM pipelines")!);
        Assert.Equal(1L, (long)RawScalar("SELECT COUNT(*) FROM task_mappings")!);
        Assert.Equal(1L, (long)RawScalar("SELECT COUNT(*) FROM conversation_entries")!);
        Assert.Equal(1L, (long)RawScalar("SELECT COUNT(*) FROM completion_receipts")!);
        Assert.Equal(1L, (long)RawScalar("SELECT COUNT(*) FROM worker_assignment_contexts")!);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (10) TEST-ONLY: a loaded context plus a matching TaskResult can build the EXISTING receipt
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE TEST-ONLY RECOVERABILITY DEMONSTRATION. With NO live worker and NO active-task queue — none
    /// exists in this fixture, and the store has no reference to either — a context loaded from durable
    /// storage plus a matching <see cref="TaskResult"/> constructs the EXISTING
    /// <see cref="CompletionReceipt"/> with the correct goal, worker, role, slot and ORIGINAL ASSIGNED
    /// model attribution, and it encodes canonically.
    /// </summary>
    /// <remarks>
    /// WHAT THIS PROVES: the recorded context is RECOVERABLE and COMPLETE enough for the existing receipt
    /// carrier to be rebuilt from storage alone. WHAT IT DOES NOT PROVE: authorization, replay
    /// acceptance, worker liveness or delivery — the receipt carrier's own constructor says exactly that,
    /// and this demonstration makes no stronger claim. Nothing here wires the store into production, and
    /// no worker, queue, dispatcher or gateway is involved.
    /// </remarks>
    [Fact]
    public void LoadedContext_PlusMatchingTaskResult_ConstructsExistingCompletionReceipt_TestOnly()
    {
        const string taskId = "task-recover-demo";
        const string assignedModel = "copilot/claude-sonnet-4.6";
        var recorded = new WorkerAssignmentContext(
            "goal-recover", "worker-recover", WorkerRole.Tester,
            new WorkSlot(taskId, new WorkSlotPosition(4, GoalPhase.Testing, 2), 3), assignedModel);

        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, store.InsertOnce(recorded).Status);

        var loaded = store.Load(taskId);

        Assert.NotNull(loaded);
        var context = loaded!.Context;

        // THE MATCHING RESULT: it carries the SAME task id and the SAME assigned model the context
        // recorded — no live worker produced it, and none is consulted.
        var result = new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "tests written and passing",
            Model = context.Model,
        };

        // THE EXISTING CARRIER CONSTRUCTS from the recovered context alone.
        var receipt = new CompletionReceipt(context.GoalId, context.WorkerId, context.Role, context.Slot, result);

        Assert.Equal("goal-recover", receipt.GoalId);
        Assert.Equal("worker-recover", receipt.WorkerId);
        Assert.Equal(WorkerRole.Tester, receipt.Role);
        Assert.Equal(context.Slot, receipt.Slot); // the WHOLE slot identity: task, iteration, phase, occurrence, attempt
        Assert.Equal(taskId, receipt.Slot.TaskId);
        Assert.Equal(4, receipt.Slot.Position!.Iteration);
        Assert.Equal(GoalPhase.Testing, receipt.Slot.Position.Phase);
        Assert.Equal(2, receipt.Slot.Position.Occurrence);
        Assert.Equal(3, receipt.Slot.Attempt);
        // THE ORIGINAL ASSIGNED MODEL ATTRIBUTION survives the recovery.
        Assert.Equal(assignedModel, receipt.Result.Model);
        Assert.Equal(assignedModel, loaded.Context.Model);

        // …and the recovered attribution survives the EXISTING canonical codec, so the rebuilt receipt is
        // a real, storable receipt — not a lookalike.
        var canonical = CompletionReceiptCodec.Encode(receipt);
        Assert.Contains("\"role\":\"tester\"", canonical, StringComparison.Ordinal);
        Assert.Contains("\"phase\":\"testing\"", canonical, StringComparison.Ordinal);
        Assert.Contains($"\"model\":\"{assignedModel}\"", canonical, StringComparison.Ordinal);
        Assert.Contains("\"attempt\":3", canonical, StringComparison.Ordinal);
        Assert.Equal(canonical, CompletionReceiptCodec.Encode(CompletionReceiptCodec.Decode(canonical)));
    }

    /// <summary>
    /// A WRONG TASK, A WRONG WORKER OR A DIFFERENT CONTEXT IS NOT A MATCHING STORED ASSIGNMENT. A result
    /// whose task id differs from the loaded slot's cannot form a receipt at all; a DIFFERENT worker's
    /// context for the same task id is rejected as a conflict against the stored one; and an unknown task
    /// id has no recorded context at all. None of these is treated as a matching assignment.
    /// </summary>
    [Fact]
    public void WrongTaskWorkerOrContext_IsNotTreatedAsAMatchingStoredAssignment()
    {
        const string taskId = "task-demote";
        var recorded = Context(goalId: "goal-demote", workerId: "worker-demote", taskId: taskId, model: "model-demote");

        var factory = NewFactory();
        var store = NewStore(factory, new AssignmentClock(FixedNow));
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, store.InsertOnce(recorded).Status);

        var context = store.Load(taskId)!.Context;

        // (a) A WRONG TASK: a result for another task id cannot be attached to the stored slot.
        var wrongTask = new TaskResult { TaskId = "task-somewhere-else", Status = TaskOutcome.Completed, Model = context.Model };
        var mismatch = Assert.Throws<ArgumentException>(
            () => new CompletionReceipt(context.GoalId, context.WorkerId, context.Role, context.Slot, wrongTask));
        Assert.Equal("result", mismatch.ParamName);

        // (b) A WRONG WORKER for the SAME task id is NOT the stored assignment: the store reports a
        // conflict rather than accepting it as a duplicate, and the stored row is unchanged.
        var otherWorker = Context(goalId: "goal-demote", workerId: "worker-impostor", taskId: taskId, model: "model-demote");
        Assert.Equal(WorkerAssignmentWriteStatus.Conflict, store.InsertOnce(otherWorker).Status);
        Assert.Equal("worker-demote", RawColumn(taskId, "worker_id"));
        Assert.NotEqual(otherWorker, context);

        // (c) A DIFFERENT CONTEXT for the same task id (a different model) is likewise NOT a match.
        var otherModel = Context(goalId: "goal-demote", workerId: "worker-demote", taskId: taskId, model: "model-other");
        Assert.Equal(WorkerAssignmentWriteStatus.Conflict, store.InsertOnce(otherModel).Status);
        Assert.Equal("model-demote", RawColumn(taskId, "model"));

        // (d) AN UNKNOWN TASK ID has NO recorded assignment — absence, never an invented one.
        Assert.Null(store.Load("task-never-recorded"));

        // (e) …and the original assignment still loads intact after every rejected attempt.
        Assert.Equal(recorded, store.Load(taskId)!.Context);
        Assert.Equal(1L, (long)RawScalar("SELECT COUNT(*) FROM worker_assignment_contexts")!);
    }

    // ───────────────────────────── fixture types ─────────────────────────────

    /// <summary>A clock frozen at one instant, so the stored timestamp text is predictable.</summary>
    private sealed class AssignmentClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A clock that always throws the pre-created sentinel.</summary>
    private sealed class AssignmentClockThrows(Exception sentinel) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw sentinel;
    }

    /// <summary>
    /// A factory handing out store-OWNED contexts, each on its own connection to the file-backed
    /// database. The contexts own those connections (they were created from a connection string), so
    /// disposing the context really releases the file handle.
    /// </summary>
    private sealed class AssignmentContextFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly string _connectionString;
        private readonly IInterceptor[] _interceptors;
        private readonly List<CopilotHiveDbContext> _contexts = [];
        private int _createCount;

        public AssignmentContextFactory(string connectionString, IInterceptor[] interceptors)
        {
            _connectionString = connectionString;
            _interceptors = interceptors;
        }

        public int CreateCount => Volatile.Read(ref _createCount);

        public CopilotHiveDbContext CreateDbContext()
        {
            Interlocked.Increment(ref _createCount);

            var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(_connectionString);
            if (_interceptors.Length > 0)
                builder.AddInterceptors(_interceptors);

            var context = new CopilotHiveDbContext(builder.Options);
            lock (_contexts)
                _contexts.Add(context);
            return context;
        }

        public void Dispose()
        {
            List<CopilotHiveDbContext> contexts;
            lock (_contexts)
                contexts = [.. _contexts];

            foreach (var context in contexts)
            {
                try
                {
                    context.Dispose();
                }
                catch
                {
                    // Best-effort — a leftover context must never fail a test.
                }
            }
        }
    }

    /// <summary>A factory whose acquisition always throws the pre-created sentinel.</summary>
    private sealed class AssignmentThrowingContextFactory(Exception sentinel) : IDbContextFactory<CopilotHiveDbContext>
    {
        public CopilotHiveDbContext CreateDbContext() => throw sentinel;
    }
}

/// <summary>A captured command attempt: its kind, its SQL text and its bound parameters.</summary>
internal sealed record AssignmentCapturedCommand(
    string Kind,
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters);

/// <summary>
/// Captures every command attempt issued through a context — the parameterization and
/// no-pre-read/no-transaction evidence.
/// </summary>
internal sealed class AssignmentCommandCaptureInterceptor : DbCommandInterceptor
{
    private readonly List<AssignmentCapturedCommand> _commands = [];

    public IReadOnlyList<AssignmentCapturedCommand> Commands
    {
        get
        {
            lock (_commands)
                return [.. _commands];
        }
    }

    private void Record(DbCommand command, string kind)
    {
        var parameters = command.Parameters.Cast<DbParameter>().ToDictionary(
            parameter => parameter.ParameterName,
            parameter => parameter.Value,
            StringComparer.Ordinal);

        lock (_commands)
            _commands.Add(new AssignmentCapturedCommand(kind, command.CommandText, parameters));
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command, "NonQuery");
        return result;
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command, "Reader");
        return result;
    }

    /// <inheritdoc />
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command, "Scalar");
        return result;
    }
}

/// <summary>Counts every transaction start/commit/rollback the context attempts.</summary>
internal sealed class AssignmentTransactionCountInterceptor : DbTransactionInterceptor
{
    private int _startCount;
    private int _commitCount;
    private int _rollbackCount;

    public int StartCount => Volatile.Read(ref _startCount);
    public int CommitCount => Volatile.Read(ref _commitCount);
    public int RollbackCount => Volatile.Read(ref _rollbackCount);

    /// <inheritdoc />
    public override InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        Interlocked.Increment(ref _startCount);
        return result;
    }

    /// <inheritdoc />
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        Interlocked.Increment(ref _commitCount);
    }

    /// <inheritdoc />
    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        Interlocked.Increment(ref _rollbackCount);
    }
}

/// <summary>Where the injected insert fault fires.</summary>
internal enum AssignmentInsertFault
{
    /// <summary>Before the provider executes the statement — nothing is written.</summary>
    BeforeExecution,

    /// <summary>After the provider executed the statement but before the commit — rolled back on dispose.</summary>
    AfterExecution,
}

/// <summary>
/// Throws a pre-created sentinel at the assignment-context INSERT, either BEFORE the provider executes it
/// (<c>NonQueryExecuting</c>) or AFTER it executed but before the transaction committed
/// (<c>NonQueryExecuted</c>). The sentinel is a single instance exposed for identity assertions, and
/// <see cref="FireCount"/> proves the injection really fired. <see cref="Disarm"/> lets a test prove that
/// an explicit retry settles.
/// </summary>
internal sealed class AssignmentInsertThrowingInterceptor : DbCommandInterceptor
{
    private readonly AssignmentInsertFault _fault;
    private int _armed = 1;
    private int _fireCount;

    public AssignmentInsertThrowingInterceptor(AssignmentInsertFault fault) => _fault = fault;

    /// <summary>The pre-created instance every armed fault throws.</summary>
    public InvalidOperationException Sentinel { get; } = new("worker-assignment-context insert SENTINEL");

    /// <summary>How many times the sentinel was thrown (the injection really fired).</summary>
    public int FireCount => Volatile.Read(ref _fireCount);

    /// <summary>Stops the injection so a subsequent call can settle.</summary>
    public void Disarm() => Volatile.Write(ref _armed, 0);

    private bool IsAssignmentInsert(DbCommand command) =>
        command.CommandText.TrimStart().StartsWith("INSERT INTO worker_assignment_contexts", StringComparison.OrdinalIgnoreCase);

    private void ThrowIfTargeted(DbCommand command)
    {
        if (Volatile.Read(ref _armed) == 0 || !IsAssignmentInsert(command))
            return;

        Interlocked.Increment(ref _fireCount);
        throw Sentinel;
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        if (_fault == AssignmentInsertFault.BeforeExecution)
            ThrowIfTargeted(command);
        return result;
    }

    /// <inheritdoc />
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        if (_fault == AssignmentInsertFault.AfterExecution)
            ThrowIfTargeted(command);
        return result;
    }
}

/// <summary>
/// Throws a pre-created sentinel ONCE THE UNDERLYING COMMIT HAS ALREADY LANDED — the genuine
/// "after-autocommit" mechanism for a store that owns its transaction. Because EF raises the committed
/// notification only AFTER the provider's commit returned, the row really is durable when the fault
/// fires, so a store that reported "no mutation" here would be lying; the readback is what establishes
/// the truth. <see cref="FireCount"/> proves the injection really fired and <see cref="Disarm"/> lets a
/// test prove that an explicit retry settles.
/// </summary>
internal sealed class AssignmentCommitThrowingInterceptor : DbTransactionInterceptor
{
    private int _armed = 1;
    private int _fireCount;

    /// <summary>The pre-created instance every armed fault throws.</summary>
    public InvalidOperationException Sentinel { get; } = new("worker-assignment-context commit SENTINEL");

    /// <summary>How many times the sentinel was thrown (the injection really fired).</summary>
    public int FireCount => Volatile.Read(ref _fireCount);

    /// <summary>Stops the injection so a subsequent call can settle.</summary>
    public void Disarm() => Volatile.Write(ref _armed, 0);

    /// <inheritdoc />
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (Volatile.Read(ref _armed) == 0)
            return;

        Interlocked.Increment(ref _fireCount);
        throw Sentinel;
    }
}

/// <summary>
/// Substitutes the provider's post-execution affected-row count for the assignment-context INSERT — the
/// unexpected-count (never a success) vector. <see cref="OverrideCount"/> proves the substitution really
/// happened.
/// </summary>
internal sealed class AssignmentRowCountInterceptor : DbCommandInterceptor
{
    private readonly int _forcedCount;
    private int _armed = 1;
    private int _overrideCount;

    public AssignmentRowCountInterceptor(int forcedCount) => _forcedCount = forcedCount;

    /// <summary>How many times the count was substituted.</summary>
    public int OverrideCount => Volatile.Read(ref _overrideCount);

    /// <summary>Stops the substitution so a subsequent call can settle.</summary>
    public void Disarm() => Volatile.Write(ref _armed, 0);

    /// <inheritdoc />
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        if (Volatile.Read(ref _armed) == 0)
            return result;

        if (!command.CommandText.TrimStart().StartsWith(
                "INSERT INTO worker_assignment_contexts", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        Interlocked.Increment(ref _overrideCount);
        return _forcedCount;
    }
}

/// <summary>
/// SUPPRESSES the provider's execution of the assignment-context INSERT and reports a genuine zero-row
/// result — the vanished-duplicate-row vector. Unlike a count override this really PREVENTS the write, so
/// the zero count and the absent row AGREE, which is exactly the inconsistent state the store's
/// missing-row guard exists for. <see cref="SuppressCount"/> proves the suppression really happened, so a
/// test using it cannot pass vacuously by never reaching the branch.
/// </summary>
internal sealed class AssignmentInsertSuppressingInterceptor : DbCommandInterceptor
{
    private int _suppressCount;

    /// <summary>How many times the INSERT execution was suppressed.</summary>
    public int SuppressCount => Volatile.Read(ref _suppressCount);

    private static bool IsAssignmentInsert(DbCommand command) =>
        command.CommandText.TrimStart().StartsWith(
            "INSERT INTO worker_assignment_contexts", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        if (!IsAssignmentInsert(command))
            return result;

        Interlocked.Increment(ref _suppressCount);
        return InterceptionResult<int>.SuppressWithResult(0);
    }
}

/// <summary>
/// Throws a pre-created sentinel whenever the assignment-context SELECT is about to execute — the
/// read-fault vectors (a read failure must remain a THROW, never write uncertainty).
/// </summary>
internal sealed class AssignmentSelectThrowingInterceptor : DbCommandInterceptor
{
    private int _throwCount;

    /// <summary>The pre-created instance every read fault throws.</summary>
    public InvalidOperationException Sentinel { get; } = new("worker-assignment-context read SENTINEL");

    /// <summary>How many times the sentinel was thrown (the injection really fired).</summary>
    public int ThrowCount => Volatile.Read(ref _throwCount);

    private void ThrowIfTargeted(DbCommand command)
    {
        var trimmed = command.CommandText.TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || !command.CommandText.Contains("worker_assignment_contexts", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Interlocked.Increment(ref _throwCount);
        throw Sentinel;
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        ThrowIfTargeted(command);
        return result;
    }
}

/// <summary>
/// The RACE GATE for a store that opens its OWN EXPLICIT TRANSACTION: every producer parks at the
/// <see cref="Barrier"/> when it is about to START its transaction — i.e. at the write boundary, but
/// BEFORE any write lock is held — so the race is genuinely concurrent and ordered by the rendezvous,
/// never by sleeping.
/// <para>
/// WHY THE RENDEZVOUS SITS HERE AND NOT INSIDE THE STATEMENT: the store's transaction is opened with
/// <c>BEGIN IMMEDIATE</c>, so the first producer to begin holds SQLite's write lock while it runs its
/// statement. Parking the SECOND producer at the statement would make the lock holder wait for a
/// competitor that cannot acquire the lock — a test-only deadlock, not a property of the store. Because
/// the rendezvous happens before the lock, the two <c>BEGIN</c> calls contend through the connection
/// string's FINITE busy timeout and the database's primary key then arbitrates. A rendezvous that times
/// out throws, so a regression fails loudly instead of hanging.
/// </para>
/// </summary>
internal sealed class AssignmentInsertGateInterceptor : DbTransactionInterceptor, IDisposable
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(60);

    private readonly Barrier _barrier;
    private int _arrivalCount;

    public AssignmentInsertGateInterceptor(int participants) => _barrier = new Barrier(participants);

    /// <summary>How many producers reached the gate.</summary>
    public int ArrivalCount => Volatile.Read(ref _arrivalCount);

    private void Gate()
    {
        Interlocked.Increment(ref _arrivalCount);
        if (!_barrier.SignalAndWait(GateTimeout))
            throw new InvalidOperationException("The worker-assignment-context insert gate never rendezvoused.");
    }

    /// <inheritdoc />
    public override InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        Gate();
        return result;
    }

    /// <inheritdoc />
    public void Dispose() => _barrier.Dispose();
}
