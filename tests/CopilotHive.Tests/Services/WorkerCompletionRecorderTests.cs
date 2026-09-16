using System.Reflection;

using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Services;

using CopilotHive.Tests.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests.Services;

/// <summary>
/// The COMPLETION-RECEIPT RECORDER, end to end against a REAL file-backed SQLite database: the
/// recorded-context agreement rules, the insert-once outcome mapping
/// (<c>Stored</c>/<c>AlreadyStored</c>/<c>Conflict</c>/<c>Indeterminate</c>), the read/codec failure
/// separation, and the fact that the retained receipt carries the STORED slot and the transport's
/// OWN selected model rather than anything reconstructed from the task id.
/// </summary>
/// <remarks>
/// <para>
/// IT DRIVES THE REAL STORES. Every fixture constructs the PRODUCTION
/// <see cref="WorkerAssignmentContextStore"/> and <see cref="CompletionReceiptStore"/> over the
/// fixture's own database and calls <see cref="WorkerCompletionRecorder.Record"/> directly. The
/// store's <c>Load</c> is used ONLY as test observation.
/// </para>
/// <para>
/// NO DUPLICATED ALGORITHM MATRIX. The codec matrix, the store's duplicate/uncertainty matrix and
/// the assignment store's own validation belong to their own suites; what is proven here is the
/// RECORDER's own contract — which stored facts reach the receipt, and which disagreements refuse.
/// </para>
/// <para>
/// THE READBACKS ARE GENUINE FRESH OPENS of the database FILE (contexts come from a connection string
/// with <c>Pooling=False</c>), so a retained receipt is proven durable across a store instance
/// boundary rather than merely visible through a live context.
/// </para>
/// </remarks>
public sealed class WorkerCompletionRecorderTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"copilothive-recorder-{Guid.NewGuid():N}.db");

    private readonly List<IDisposable> _fixtures = [];

    public WorkerCompletionRecorderTests()
    {
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

    private string ConnectionString => $"Data Source={_dbPath};Pooling=False;Default Timeout=15";

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private static CopilotHiveDbContext ContextOn(
        SqliteConnection connection, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);
        return new CopilotHiveDbContext(builder.Options);
    }

    private RecordingContextFactory NewFactory(params IInterceptor[] interceptors)
    {
        var factory = new RecordingContextFactory(ConnectionString, interceptors);
        _fixtures.Add(factory);
        return factory;
    }

    /// <summary>A REAL recorder over the fixture's REAL assignment-context and receipt stores.</summary>
    private WorkerCompletionRecorder NewRecorder(IDbContextFactory<CopilotHiveDbContext> factory) =>
        new(
            new WorkerAssignmentContextStore(factory, NullLogger<WorkerAssignmentContextStore>.Instance),
            new CompletionReceiptStore(factory, NullLogger<CompletionReceiptStore>.Instance));

    private WorkerAssignmentContextStore NewAssignmentStore(
        IDbContextFactory<CopilotHiveDbContext> factory) =>
        new(factory, NullLogger<WorkerAssignmentContextStore>.Instance);

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

    private long ReceiptRowCount(string taskId) =>
        (long)RawScalar($"SELECT COUNT(*) FROM completion_receipts WHERE task_id = '{Escape(taskId)}'")!;

    private string? RawReceiptPayload(string taskId) =>
        (string?)RawScalar($"SELECT payload_json FROM completion_receipts WHERE task_id = '{Escape(taskId)}'");

    private string? RawFirstStoredText(string taskId) =>
        (string?)RawScalar($"SELECT first_stored_at_utc FROM completion_receipts WHERE task_id = '{Escape(taskId)}'");

    private long AssignmentRowCount =>
        (long)RawScalar("SELECT COUNT(*) FROM worker_assignment_contexts")!;

    // ───────────────────────────── the subject's inputs ─────────────────────────────

    private const string WorkerId = "worker-recorder";
    private const string GoalId = "goal-recorder";

    /// <summary>
    /// THE OPAQUE TASK ID: a legacy-looking form with no parseable structure, so nothing in the
    /// recorder can be reconstructing identity from its text.
    /// </summary>
    private const string TaskId = "t7f3::opaque/../no-structure";

    /// <summary>
    /// The ACTIVE task the transport validated. Its <see cref="CopilotHive.Services.WorkTask.Model"/>
    /// is deliberately DIFFERENT from the stored assignment's model, and its
    /// <see cref="CopilotHive.Services.WorkTask.Iteration"/> deliberately disagrees with the stored
    /// slot — neither may reach the receipt.
    /// </summary>
    private static CopilotHive.Services.WorkTask ActiveTask() => new()
    {
        TaskId = TaskId,
        GoalId = GoalId,
        GoalDescription = "record the completion",
        Prompt = "do the work",
        Role = WorkerRole.Coder,
        Model = "queue-model",
        Iteration = 99,
        Repositories = [],
    };

    /// <summary>The already-mapped result the transport produced, carrying ITS selected model.</summary>
    private static CopilotHive.Services.TaskResult MappedResult(string model = "selected-model") => new()
    {
        TaskId = TaskId,
        Status = TaskOutcome.Completed,
        Output = "completed output",
        Model = model,
        IterationStartSha = "abc123",
        Metrics = new CopilotHive.Services.TaskMetrics
        {
            Verdict = "PASS",
            BuildSuccess = true,
            TotalTests = 11,
            PassedTests = 10,
            FailedTests = 1,
            CoveragePercent = 88.5,
            Issues = ["issue-a"],
            Summary = "summary",
        },
        GitStatus = new CopilotHive.Services.GitChangeSummary
        {
            FilesChanged = 4,
            Insertions = 40,
            Deletions = 2,
            Pushed = true,
            ChangedFiles = ["src/a.cs"],
        },
    };

    /// <summary>
    /// The AUTHORITATIVE stored slot: a REPEATED phase position (iteration 2, the second Coding
    /// occurrence) with a non-first attempt, so a receipt that reconstructed its position from the
    /// task id (or defaulted it to one) cannot reproduce it.
    /// </summary>
    private static WorkSlot StoredSlot(string taskId = TaskId) =>
        new(taskId, new WorkSlotPosition(2, GoalPhase.Coding, 2), 3);

    /// <summary>
    /// Records a GENUINE assignment context through the REAL store and asserts it landed — the
    /// "valid assignment setup" every accepting vector needs.
    /// </summary>
    private WorkerAssignmentContext SeedRecordedContext(
        IDbContextFactory<CopilotHiveDbContext> factory,
        string taskId = TaskId,
        string? workerId = null,
        string? goalId = null,
        WorkerRole role = WorkerRole.Coder,
        WorkSlot? slot = null,
        string model = "assigned-model")
    {
        var context = new WorkerAssignmentContext(
            goalId ?? GoalId,
            workerId ?? WorkerId,
            role,
            slot ?? StoredSlot(taskId),
            model);

        var write = NewAssignmentStore(factory).InsertOnce(context);
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, write.Status);
        return context;
    }

    /// <summary>Seeds a receipt row DIRECTLY, bypassing the store — the conflict/corruption setup.</summary>
    private void SeedReceiptRow(string taskId, string goalId, string payloadJson, string firstStoredText) =>
        ExecuteRaw(
            "INSERT INTO completion_receipts (task_id, goal_id, payload_json, first_stored_at_utc) " +
            $"VALUES ('{Escape(taskId)}', '{Escape(goalId)}', '{Escape(payloadJson)}', '{Escape(firstStoredText)}')");

    private static readonly string SeededFirstStoredText =
        new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero)
            .UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    // ═══════════════════════════════════════════════════════════════════════
    // (1) The retained receipt — durable, and built from the STORED facts
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A GENUINE STORED CONTEXT AND A MAPPED RESULT produce a DURABLE receipt whose canonical text
    /// survives a FRESH store instance over the same file, and whose identity is the STORED facts:
    /// the authoritative slot position/attempt (never <c>WorkTask.Iteration</c>), the stored
    /// goal/worker/role, and the RESULT's own model (never the stored assignment's model).
    /// </summary>
    /// <remarks>
    /// DISCRIMINATING: a recorder that reconstructed the position from the task id's text, used
    /// <c>WorkTask.Iteration</c> (99), or overwrote the result's model with the stored assignment's
    /// model ("assigned-model") fails here, and a receipt that was not durable fails the fresh-store
    /// readback.
    /// </remarks>
    [Fact]
    public void Record_StoredContext_WritesReceiptThatSurvivesFreshStoreReadback()
    {
        var writeFactory = NewFactory();
        SeedRecordedContext(writeFactory);

        var recorder = NewRecorder(writeFactory);
        recorder.Record(WorkerId, ActiveTask(), MappedResult());

        // THE ROW IS DURABLE ON DISK, one row, with the codec's canonical payload.
        Assert.Equal(1L, ReceiptRowCount(TaskId));

        // DISPOSE every fixture before reading anything back.
        writeFactory.Dispose();

        var readFactory = NewFactory();
        var loaded = new CompletionReceiptStore(
            readFactory, NullLogger<CompletionReceiptStore>.Instance).Load(TaskId);

        Assert.NotNull(loaded);
        var receipt = loaded!.Receipt;

        // THE STORED IDENTITY.
        Assert.Equal(GoalId, receipt.GoalId);
        Assert.Equal(WorkerId, receipt.WorkerId);
        Assert.Equal(WorkerRole.Coder, receipt.Role);
        Assert.Equal(TaskId, receipt.Slot.TaskId);

        // THE AUTHORITATIVE SLOT — the repeated position and the non-first attempt.
        Assert.Equal(2, receipt.Slot.Position!.Iteration);
        Assert.Equal(GoalPhase.Coding, receipt.Slot.Position.Phase);
        Assert.Equal(2, receipt.Slot.Position.Occurrence);
        Assert.Equal(3, receipt.Slot.Attempt);

        // THE COMPLETE MAPPED RESULT, including the model the TRANSPORT selected.
        Assert.Equal(TaskOutcome.Completed, receipt.Result.Status);
        Assert.Equal("completed output", receipt.Result.Output);
        Assert.Equal("selected-model", receipt.Result.Model);
        Assert.Equal("abc123", receipt.Result.IterationStartSha);
        Assert.Equal("PASS", receipt.Result.Metrics!.Verdict);
        Assert.Equal(11, receipt.Result.Metrics.TotalTests);
        Assert.Equal(10, receipt.Result.Metrics.PassedTests);
        Assert.Equal(1, receipt.Result.Metrics.FailedTests);
        Assert.Equal(88.5, receipt.Result.Metrics.CoveragePercent);
        Assert.Equal(["issue-a"], receipt.Result.Metrics.Issues);
        Assert.Equal("summary", receipt.Result.Metrics.Summary);
        Assert.Equal(4, receipt.Result.GitStatus!.FilesChanged);
        Assert.True(receipt.Result.GitStatus.Pushed);
        Assert.Equal(["src/a.cs"], receipt.Result.GitStatus.ChangedFiles);
    }

    /// <summary>
    /// THE MODEL IS THE TRANSPORT'S SELECTION, VERBATIM. An EMPTY result model is retained as an
    /// empty model even though the stored assignment context carries a non-empty one, and a
    /// whitespace model is neither trimmed nor substituted.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("copilot/whitespace  ")]
    public void Record_ExplicitResultModel_IsRetainedVerbatimEvenWhenStoredModelDiffers(string selectedModel)
    {
        var factory = NewFactory();
        SeedRecordedContext(factory, model: "assigned-model");

        NewRecorder(factory).Record(WorkerId, ActiveTask(), MappedResult(selectedModel));

        var loaded = new CompletionReceiptStore(factory, NullLogger<CompletionReceiptStore>.Instance).Load(TaskId);
        Assert.NotNull(loaded);
        Assert.Equal(selectedModel, loaded!.Receipt.Result.Model);
        Assert.Equal("assigned-model", RawScalar(
            $"SELECT model FROM worker_assignment_contexts WHERE task_id = '{Escape(TaskId)}'"));
    }

    /// <summary>
    /// AN ALREADY-STORED, IDENTICAL COMPLETION settles <c>AlreadyStored</c> and returns NORMALLY: the
    /// row and its first-stored instant are untouched and nothing is duplicated. It is NOT interpreted
    /// as "already processed" — the recorder simply permits THIS invocation.
    /// </summary>
    [Fact]
    public void Record_IdenticalCompletionTwice_SecondSettlesAlreadyStoredAndRowIsUnchanged()
    {
        var factory = NewFactory();
        SeedRecordedContext(factory);

        var recorder = NewRecorder(factory);
        recorder.Record(WorkerId, ActiveTask(), MappedResult());

        var payloadAfterFirst = RawReceiptPayload(TaskId);
        var firstStoredAfterFirst = RawFirstStoredText(TaskId);
        Assert.NotNull(payloadAfterFirst);

        recorder.Record(WorkerId, ActiveTask(), MappedResult());

        Assert.Equal(1L, ReceiptRowCount(TaskId));
        Assert.Equal(payloadAfterFirst, RawReceiptPayload(TaskId));
        Assert.Equal(firstStoredAfterFirst, RawFirstStoredText(TaskId));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) Refusals that write NOTHING
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A MISSING STORED CONTEXT REFUSES: no receipt is retained AND no assignment context is
    /// synthesized for the never-recorded task.
    /// </summary>
    [Fact]
    public void Record_WithoutStoredContext_RefusesAndWritesNothing()
    {
        var factory = NewFactory();
        var recorder = NewRecorder(factory);

        var refusal = Assert.Throws<WorkerCompletionRecordingException>(
            () => recorder.Record(WorkerId, ActiveTask(), MappedResult()));

        Assert.Equal(WorkerCompletionRecordingFailureReason.InvalidContext, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.Null(refusal.InnerException);
        Assert.Contains(TaskId, refusal.Message, StringComparison.Ordinal);

        Assert.Equal(0L, ReceiptRowCount(TaskId));
        Assert.Equal(0L, AssignmentRowCount);
    }

    /// <summary>
    /// A COMPLETING RESULT THAT NAMES A DIFFERENT TASK THAN THE VALIDATED ACTIVE TASK REFUSES, before
    /// any store is consulted.
    /// </summary>
    [Fact]
    public void Record_ResultTaskIdDiffersFromActiveTask_RefusesAndWritesNothing()
    {
        var factory = NewFactory();
        SeedRecordedContext(factory);

        var mismatched = MappedResult() with { TaskId = "some-other-task" };

        var refusal = Assert.Throws<WorkerCompletionRecordingException>(
            () => NewRecorder(factory).Record(WorkerId, ActiveTask(), mismatched));

        Assert.Equal(WorkerCompletionRecordingFailureReason.InvalidContext, refusal.Reason);
        Assert.Null(refusal.InnerException);
        Assert.Equal(0L, ReceiptRowCount(TaskId));
        Assert.Equal(0L, ReceiptRowCount("some-other-task"));
    }

    /// <summary>
    /// A STORED CONTEXT WHOSE WORKER, GOAL OR ROLE DISAGREES WITH THE COMPLETING TASK AND THE PINNED
    /// WORKER REFUSES — ordinally, case-sensitively — and retains no receipt. Every cell asserts the
    /// DISAGREEING FIELD by name, so a recorder that dropped one of the checks fails.
    /// </summary>
    /// <remarks>
    /// The role cell is seeded with the phase-mapped role of a DIFFERENT worker-backed phase
    /// (<see cref="WorkerRole.Tester"/> for Testing), which the assignment context's own constructor
    /// requires, so the disagreement is between the stored context and the ACTIVE task's role.
    /// </remarks>
    [Theory]
    [InlineData("worker-case")]
    [InlineData("worker-other")]
    [InlineData("goal-case")]
    [InlineData("goal-other")]
    [InlineData("role-other")]
    public void Record_StoredContextDisagrees_RefusesAndWritesNoReceipt(string cell)
    {
        var factory = NewFactory();

        var (task, result) = cell switch
        {
            "worker-case" => (ActiveTask(), MappedResult()),
            "worker-other" => (ActiveTask(), MappedResult()),
            "goal-case" => (ActiveTask(), MappedResult()),
            "goal-other" => (ActiveTask(), MappedResult()),
            "role-other" => (
                ActiveTask() with { Role = WorkerRole.Coder },
                MappedResult()),
            _ => throw new InvalidOperationException($"Unknown cell '{cell}'."),
        };

        switch (cell)
        {
            case "worker-case":
                // The SAME worker id in a different case: ordinal comparison must refuse it.
                SeedRecordedContext(factory, workerId: WorkerId.ToUpperInvariant());
                break;
            case "worker-other":
                SeedRecordedContext(factory, workerId: "worker-somewhere-else");
                break;
            case "goal-case":
                SeedRecordedContext(factory, goalId: GoalId.ToUpperInvariant());
                break;
            case "goal-other":
                SeedRecordedContext(factory, goalId: "goal-somewhere-else");
                break;
            case "role-other":
                // A stored context for the TESTING phase (role Tester) while the active task is a
                // CODER task — a genuine, constructible disagreement.
                SeedRecordedContext(
                    factory,
                    role: WorkerRole.Tester,
                    slot: new WorkSlot(TaskId, new WorkSlotPosition(2, GoalPhase.Testing, 2), 3));
                break;
            default:
                throw new InvalidOperationException($"Unknown cell '{cell}'.");
        }

        var refusal = Assert.Throws<WorkerCompletionRecordingException>(
            () => NewRecorder(factory).Record(WorkerId, task, result));

        Assert.Equal(WorkerCompletionRecordingFailureReason.InvalidContext, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.Null(refusal.InnerException);
        Assert.Contains(TaskId, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0L, ReceiptRowCount(TaskId));
        Assert.Equal(1L, AssignmentRowCount);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) Conflict and write uncertainty through the REAL store
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A DIFFERENT RECEIPT ALREADY RETAINED for the same task id yields
    /// <see cref="WorkerCompletionRecordingFailureReason.Conflict"/> carrying the store's reported
    /// status, and NOTHING changes: the existing payload and its first-stored instant are untouched.
    /// </summary>
    [Fact]
    public void Record_DifferentReceiptAlreadyRetained_ConflictLeavesExistingRowUnchanged()
    {
        var factory = NewFactory();
        SeedRecordedContext(factory);

        // A genuinely different, WELL-FORMED receipt for the same task id.
        var existing = new CompletionReceipt(
            GoalId,
            WorkerId,
            WorkerRole.Coder,
            StoredSlot(),
            MappedResult() with { Output = "an-earlier-output" });
        SeedReceiptRow(
            TaskId, GoalId, CompletionReceiptCodec.Encode(existing), SeededFirstStoredText);
        var payloadBefore = RawReceiptPayload(TaskId);

        var refusal = Assert.Throws<WorkerCompletionRecordingException>(
            () => NewRecorder(factory).Record(WorkerId, ActiveTask(), MappedResult()));

        Assert.Equal(WorkerCompletionRecordingFailureReason.Conflict, refusal.Reason);
        Assert.Equal(CompletionReceiptWriteStatus.Conflict, refusal.StoreStatus);
        Assert.Null(refusal.InnerException);
        Assert.Contains(TaskId, refusal.Message, StringComparison.Ordinal);

        Assert.Equal(1L, ReceiptRowCount(TaskId));
        Assert.Equal(payloadBefore, RawReceiptPayload(TaskId));
        Assert.Equal(SeededFirstStoredText, RawFirstStoredText(TaskId));
    }

    /// <summary>
    /// COMMITTED-BUT-REPORTED-INDETERMINATE, then an IDENTICAL completion: the injected
    /// post-execution fault makes the FIRST record report
    /// <see cref="WorkerCompletionRecordingFailureReason.Indeterminate"/> with the EXACT sentinel,
    /// and the SECOND (identical) record then settles <c>AlreadyStored</c> — with the stored payload
    /// and first-stored time unchanged.
    /// </summary>
    /// <remarks>
    /// THE INJECTION IS THE EXISTING EF INTERCEPTOR FACILITY (a command interceptor that throws after
    /// the provider executed the statement), so the uncertainty is genuine: the autocommit really
    /// landed and the store really made no claim about it.
    /// </remarks>
    [Fact]
    public void Record_WriteUncertaintyThenIdenticalCompletion_IndeterminateThenAlreadyStored()
    {
        var interceptor = new ReceiptInsertThrowingInterceptor(ReceiptInsertFault.AfterExecution);
        var factory = NewFactory(interceptor);
        SeedRecordedContext(factory);

        var recorder = NewRecorder(factory);

        var refusal = Assert.Throws<WorkerCompletionRecordingException>(
            () => recorder.Record(WorkerId, ActiveTask(), MappedResult()));

        Assert.Equal(WorkerCompletionRecordingFailureReason.Indeterminate, refusal.Reason);
        Assert.Equal(CompletionReceiptWriteStatus.Indeterminate, refusal.StoreStatus);
        Assert.Same(interceptor.Sentinel, refusal.InnerException);
        Assert.Equal(1, interceptor.FireCount);

        // The row IS durable (the fault fired after the autocommit) and the recorder claimed nothing.
        Assert.Equal(1L, ReceiptRowCount(TaskId));
        var payloadAfterUncertainWrite = RawReceiptPayload(TaskId);
        var firstStoredAfterUncertainWrite = RawFirstStoredText(TaskId);

        // AN IDENTICAL LATER COMPLETION SETTLES AlreadyStored — and returns NORMALLY.
        interceptor.Disarm();
        recorder.Record(WorkerId, ActiveTask(), MappedResult());

        Assert.Equal(1L, ReceiptRowCount(TaskId));
        Assert.Equal(payloadAfterUncertainWrite, RawReceiptPayload(TaskId));
        Assert.Equal(firstStoredAfterUncertainWrite, RawFirstStoredText(TaskId));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (4) Store failures retain their EXACT evidence
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A READ/QUERY FAILURE OF THE ASSIGNMENT STORE is a <c>StoreError</c> retaining the EXACT caught
    /// exception — never a Conflict, never an Indeterminate and never a fabricated one. The factory
    /// points at an unusable database file, so the read genuinely fails at the provider boundary.
    /// </summary>
    [Fact]
    public void Record_AssignmentReadThrows_StoreErrorWithExactEvidenceAndNoReceipt()
    {
        var unusable = new UnusableDatabaseFactory();

        var recorder = NewRecorder(unusable);

        var refusal = Assert.Throws<WorkerCompletionRecordingException>(
            () => recorder.Record(WorkerId, ActiveTask(), MappedResult()));

        Assert.Equal(WorkerCompletionRecordingFailureReason.StoreError, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.NotNull(refusal.InnerException);
        // THE EXACT caught provider exception — its message names the unusable file, so a
        // reconstructed or generic wrapper cannot satisfy these assertions.
        Assert.IsAssignableFrom<SqliteException>(refusal.InnerException);
        Assert.Contains(
            "unable to open database file",
            refusal.InnerException!.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0L, ReceiptRowCount(TaskId));
    }

    /// <summary>
    /// A CODEC FAILURE ON THE DUPLICATE PATH is a <c>StoreError</c> carrying the codec's own exception
    /// object — it is never dressed up as write uncertainty. The corrupt row is seeded directly, so
    /// the zero-row insert really reaches the store's decode.
    /// </summary>
    /// <remarks>
    /// The seed is a valid-JSON envelope with a MISSING required member, which the codec refuses; the
    /// inserted statement conflicts with it (zero rows) and the readback then throws.
    /// </remarks>
    [Fact]
    public void Record_StoredPayloadUnusable_StoreErrorWithCodecEvidenceAndNoChange()
    {
        var factory = NewFactory();
        SeedRecordedContext(factory);
        SeedReceiptRow(TaskId, GoalId, """{"version":1}""", SeededFirstStoredText);

        var refusal = Assert.Throws<WorkerCompletionRecordingException>(
            () => NewRecorder(factory).Record(WorkerId, ActiveTask(), MappedResult()));

        Assert.Equal(WorkerCompletionRecordingFailureReason.StoreError, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.IsType<CompletionReceiptCodecException>(refusal.InnerException);

        // The unusable row is left exactly as it was — nothing is repaired or replaced.
        Assert.Equal("""{"version":1}""", RawReceiptPayload(TaskId));
        Assert.Equal(SeededFirstStoredText, RawFirstStoredText(TaskId));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (5) The production container resolves the real recorder and store
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE PRODUCTION DI REGISTRATION (verified against the REAL Program container): the concrete
    /// recorder and its narrow interface resolve to the SAME singleton, wired to the container's own
    /// assignment-context store and its own factory-only receipt store.
    /// </summary>
    [Fact]
    public void DiResolves_ConcreteRecorderInterfaceAndStores_AsTheSameSingletonGraph()
    {
        using var factory = new HiveTestFactory();
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;

        var concrete = provider.GetRequiredService<WorkerCompletionRecorder>();
        var face = provider.GetRequiredService<IWorkerCompletionRecorder>();
        var assignmentStore = provider.GetRequiredService<WorkerAssignmentContextStore>();
        var receiptStore = provider.GetRequiredService<CompletionReceiptStore>();

        // THE SAME SINGLETON behind both service types.
        Assert.Same(concrete, face);

        // THE RECORDER'S DEPENDENCIES are the container's own singletons.
        Assert.Same(assignmentStore, RecorderField(concrete, "_assignmentStore"));
        Assert.Same(receiptStore, RecorderField(concrete, "_receiptStore"));
    }

    /// <summary>
    /// THE CONTAINER'S RECORDER REALLY WORKS ON ITS OWN GRAPH: a genuine assignment context recorded
    /// through the container's own <see cref="WorkerAssignmentContextStore"/> then lets the
    /// container's own <see cref="IWorkerCompletionRecorder"/> retain a receipt that the container's
    /// own <see cref="CompletionReceiptStore"/> reads back. Nothing the recorder depends on is a
    /// fixture substitute.
    /// </summary>
    [Fact]
    public void DiRecorder_WorksEndToEndOnTheContainerGraph()
    {
        using var factory = new HiveTestFactory();
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;

        var assignmentStore = provider.GetRequiredService<WorkerAssignmentContextStore>();
        var receiptStore = provider.GetRequiredService<CompletionReceiptStore>();
        var recorder = provider.GetRequiredService<IWorkerCompletionRecorder>();

        const string taskId = "task-di-recorder";
        var context = new WorkerAssignmentContext(
            "goal-di",
            "worker-di",
            WorkerRole.Coder,
            new WorkSlot(taskId, new WorkSlotPosition(1, GoalPhase.Coding, 1), 1),
            "assigned-model");
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, assignmentStore.InsertOnce(context).Status);

        var task = new CopilotHive.Services.WorkTask
        {
            TaskId = taskId,
            GoalId = "goal-di",
            GoalDescription = "record through the container",
            Prompt = "do the work",
            Role = WorkerRole.Coder,
            Model = "queue-model",
            Repositories = [],
        };
        var result = new CopilotHive.Services.TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "container output",
            Model = "selected-model",
        };

        recorder.Record("worker-di", task, result);

        var loaded = receiptStore.Load(taskId);
        Assert.NotNull(loaded);
        Assert.Equal("goal-di", loaded!.Receipt.GoalId);
        Assert.Equal("worker-di", loaded.Receipt.WorkerId);
        Assert.Equal("selected-model", loaded.Receipt.Result.Model);
        Assert.Equal("container output", loaded.Receipt.Result.Output);
    }

    private static object RecorderField(WorkerCompletionRecorder recorder, string name) =>
        typeof(WorkerCompletionRecorder)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(recorder)!;

    // ───────────────────────────── test factories ─────────────────────────────

    /// <summary>
    /// A factory handing out store-OWNED contexts, each on its own connection to the file-backed
    /// database (contexts created from a connection string really release the file handle when
    /// disposed). <see cref="OnCreate"/> is the boundary hook a vector uses to mutate state from
    /// inside the operation under test.
    /// </summary>
    private sealed class RecordingContextFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly string _connectionString;
        private readonly IInterceptor[] _interceptors;
        private readonly List<CopilotHiveDbContext> _contexts = [];

        public RecordingContextFactory(string connectionString, IInterceptor[] interceptors)
        {
            _connectionString = connectionString;
            _interceptors = interceptors;
        }

        /// <summary>Runs INSIDE <see cref="CreateDbContext"/>.</summary>
        public Action? OnCreate { get; set; }

        public CopilotHiveDbContext CreateDbContext()
        {
            OnCreate?.Invoke();

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

    /// <summary>
    /// A factory pointing at a database directory that does not exist, so the provider genuinely fails
    /// to open the file and the store's read/query failure surfaces with ITS OWN exception.
    /// </summary>
    private sealed class UnusableDatabaseFactory : IDbContextFactory<CopilotHiveDbContext>
    {
        private readonly string _connectionString = string.Concat(
            "Data Source=",
            Path.Combine(Path.GetTempPath(), $"copilothive-recorder-missing-{Guid.NewGuid():N}"),
            Path.DirectorySeparatorChar,
            "absent.db");

        public CopilotHiveDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite(_connectionString)
                .Options);
    }
}
