using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary>
    /// The already-mapped result the transport produced, carrying ITS selected model.
    /// <para>
    /// THE TWO EVIDENCE LISTS CARRY TWO ITEMS EACH ON PURPOSE. A single-item list cannot be
    /// REORDERED, so an order-insensitive comparison could never be distinguished from the ordered
    /// one; with two items a vector can present the SAME membership and the SAME length in the
    /// OPPOSITE order (see the <c>*-reordered</c> confirmation cells).
    /// </para>
    /// </summary>
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
            Issues = ["issue-a", "issue-b"],
            Summary = "summary",
        },
        GitStatus = new CopilotHive.Services.GitChangeSummary
        {
            FilesChanged = 4,
            Insertions = 40,
            Deletions = 2,
            Pushed = true,
            ChangedFiles = ["src/a.cs", "src/b.cs"],
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
        Assert.Equal(["issue-a", "issue-b"], receipt.Result.Metrics.Issues);
        Assert.Equal("summary", receipt.Result.Metrics.Summary);
        Assert.Equal(4, receipt.Result.GitStatus!.FilesChanged);
        Assert.True(receipt.Result.GitStatus.Pushed);
        Assert.Equal(["src/a.cs", "src/b.cs"], receipt.Result.GitStatus.ChangedFiles);
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

    // ═══════════════════════════════════════════════════════════════════════
    // (6) The strictly read-only confirmation — ConfirmStoredReceipt
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A RECORD-ONLY IMPLEMENTATION STILL COMPILES AND FAILS CLOSED: the DEFAULT interface body
    /// answers <c>false</c> — "no matching evidence I can attest to" — and never calls
    /// <see cref="IWorkerCompletionRecorder.Record"/>.
    /// </summary>
    [Fact]
    public void ConfirmStoredReceipt_RecordOnlyImplementation_ReturnsFalseWithoutRecording()
    {
        var recordOnly = new RecordOnlyRecorder();
        IWorkerCompletionRecorder face = recordOnly;

        Assert.False(face.ConfirmStoredReceipt(WorkerId, TaskId, MappedResult()));
        Assert.Equal(0, recordOnly.RecordCount);
    }

    /// <summary>
    /// SEED ONLY A RECEIPT — NO ASSIGNMENT ROW AT ALL — and a REAL recorder confirms a rich,
    /// SEPARATELY ALLOCATED equivalent completion REPEATEDLY, leaving the payload, the row count, the
    /// first-stored instant and the (absent) assignment rows exactly as they were.
    /// </summary>
    /// <remarks>
    /// DISCRIMINATING: a confirmation that consulted
    /// <see cref="WorkerAssignmentContextStore.Load"/> would refuse (there is no context), a
    /// confirmation that compared something weaker than the full canonical evidence would still pass
    /// here — which is why the difference matrix below exists — and any write at all would move the
    /// row count, the payload or the first-stored text.
    /// </remarks>
    [Fact]
    public void ConfirmStoredReceipt_ReceiptOnlySeeded_ConfirmsRepeatedlyWithoutChangingTheRow()
    {
        var factory = NewFactory();
        var canonical = CompletionReceiptCodec.Encode(RetainedReceipt());
        SeedReceiptRow(TaskId, GoalId, canonical, SeededFirstStoredText);

        // THE ONLY ROW IS THE RECEIPT: confirmation needs no recorded assignment context.
        Assert.Equal(0L, AssignmentRowCount);

        var recorder = NewRecorder(factory);

        for (var attempt = 0; attempt < 3; attempt++)
            Assert.True(recorder.ConfirmStoredReceipt(WorkerId, TaskId, MappedResult()), $"attempt {attempt}");

        Assert.Equal(1L, ReceiptRowCount(TaskId));
        Assert.Equal(canonical, RawReceiptPayload(TaskId));
        Assert.Equal(SeededFirstStoredText, RawFirstStoredText(TaskId));
        Assert.Equal(0L, AssignmentRowCount);
    }

    /// <summary>
    /// CANONICALLY EQUIVALENT BUT DIFFERENTLY FORMATTED STORED JSON MATCHES: the retained payload is
    /// indented and carries case-variant enum VALUE labels, and the confirmation still answers
    /// <c>true</c> — because equality is the ORDINAL comparison of the codec's canonical texts, never
    /// the raw stored bytes. The non-canonical bytes and the first-stored instant are untouched.
    /// </summary>
    [Fact]
    public void ConfirmStoredReceipt_EquivalentNonCanonicalStoredJson_MatchesAndLeavesRawBytesUntouched()
    {
        var factory = NewFactory();
        var canonical = CompletionReceiptCodec.Encode(RetainedReceipt());

        var nonCanonical = JsonSerializer
            .Serialize(JsonNode.Parse(canonical), new JsonSerializerOptions { WriteIndented = true })
            .Replace("\"coder\"", "\"CODER\"", StringComparison.Ordinal)
            .Replace("\"coding\"", "\"Coding\"", StringComparison.Ordinal);

        Assert.NotEqual(canonical, nonCanonical);
        // The equivalence is REAL: re-encoding the decode reproduces the canonical text.
        Assert.Equal(canonical, CompletionReceiptCodec.Encode(CompletionReceiptCodec.Decode(nonCanonical)));

        SeedReceiptRow(TaskId, GoalId, nonCanonical, SeededFirstStoredText);

        Assert.True(NewRecorder(factory).ConfirmStoredReceipt(WorkerId, TaskId, MappedResult()));

        Assert.Equal(nonCanonical, RawReceiptPayload(TaskId));
        Assert.Equal(SeededFirstStoredText, RawFirstStoredText(TaskId));
        Assert.Equal(0L, AssignmentRowCount);
    }

    /// <summary>
    /// THE DIFFERENCE AND ABSENCE MATRIX: a representable difference in ANY canonical evidence — the
    /// pinned worker (ordinal, case-sensitive), the task id (ordinal, case-sensitive), the result's
    /// status/output/model/iteration SHA (including value-versus-null), the nested metrics and
    /// git-status objects (values, null-versus-object and ORDERED list contents) — answers
    /// <c>false</c>, exactly like absence. None of them is ever dressed up as a store failure, and
    /// none of them changes a single byte of the retained row.
    /// </summary>
    /// <remarks>
    /// Every difference vector first asserts its OWN precondition — the canonical text of the receipt
    /// it would produce is DIFFERENT from the retained one — so a vector that accidentally rebuilt the
    /// retained evidence cannot pass as a "difference". This is deliberately a SMALL use of the
    /// existing canonical comparison, not a duplication of the codec's own matrix.
    /// </remarks>
    [Theory]
    [InlineData("absent")]
    [InlineData("worker-case")]
    [InlineData("worker-other")]
    [InlineData("task-case")]
    [InlineData("status")]
    [InlineData("output")]
    [InlineData("model")]
    [InlineData("sha-value")]
    [InlineData("sha-null-versus-value")]
    [InlineData("metrics-coverage")]
    [InlineData("metrics-verdict")]
    [InlineData("metrics-null")]
    [InlineData("metrics-issues-extra")]
    [InlineData("metrics-issues-reordered")]
    [InlineData("git-values")]
    [InlineData("git-pushed")]
    [InlineData("git-null")]
    [InlineData("git-changed-files-extra")]
    [InlineData("git-changed-files-reordered")]
    public void ConfirmStoredReceipt_DifferenceOrAbsence_ReturnsFalseAndChangesNothing(string cell)
    {
        var factory = NewFactory();
        var canonical = CompletionReceiptCodec.Encode(RetainedReceipt());

        var workerId = WorkerId;
        var taskId = TaskId;
        var candidate = MappedResult();

        switch (cell)
        {
            case "absent":
                // NO ROW IS SEEDED AT ALL.
                break;

            case "worker-case":
                workerId = WorkerId.ToUpperInvariant();
                break;

            case "worker-other":
                workerId = "worker-somewhere-else";
                break;

            case "task-case":
                // A case-variant OPAQUE KEY: identity is ordinal, so this finds no row at all.
                taskId = TaskId.ToUpperInvariant();
                candidate = candidate with { TaskId = taskId };
                break;

            case "status":
                candidate = candidate with { Status = TaskOutcome.Failed };
                break;

            case "output":
                candidate = candidate with { Output = "a-different-output" };
                break;

            case "model":
                candidate = candidate with { Model = "a-different-model" };
                break;

            case "sha-value":
                candidate = candidate with { IterationStartSha = "def456" };
                break;

            case "sha-null-versus-value":
                candidate = candidate with { IterationStartSha = null };
                break;

            case "metrics-coverage":
                candidate = candidate with { Metrics = candidate.Metrics! with { CoveragePercent = 12.5 } };
                break;

            case "metrics-verdict":
                candidate = candidate with { Metrics = candidate.Metrics! with { Verdict = "FAIL" } };
                break;

            case "metrics-null":
                candidate = candidate with { Metrics = null };
                break;

            case "metrics-issues-extra":
                candidate = candidate with { Metrics = candidate.Metrics! with { Issues = ["issue-a", "issue-b", "extra-issue"] } };
                break;

            case "metrics-issues-reordered":
                // REORDER-ONLY: EXACTLY the retained items ("issue-a", "issue-b"), the SAME count,
                // in the OPPOSITE order. Membership and length are untouched, so an order-INSENSITIVE
                // comparison would call this a match — the canonical comparison is ORDERED, so the
                // answer must be false.
                candidate = candidate with { Metrics = candidate.Metrics! with { Issues = ["issue-b", "issue-a"] } };
                break;

            case "git-values":
                candidate = candidate with { GitStatus = candidate.GitStatus! with { FilesChanged = 9 } };
                break;

            case "git-pushed":
                candidate = candidate with { GitStatus = candidate.GitStatus! with { Pushed = false } };
                break;

            case "git-null":
                candidate = candidate with { GitStatus = null };
                break;

            case "git-changed-files-extra":
                candidate = candidate with { GitStatus = candidate.GitStatus! with { ChangedFiles = ["src/a.cs", "src/b.cs", "src/extra.cs"] } };
                break;

            case "git-changed-files-reordered":
                // REORDER-ONLY, exactly as for the issue list above: the SAME two paths, the SAME
                // count, swapped. Canonical equality is ORDERED, so this is a genuine difference and
                // not a "same set" match.
                candidate = candidate with { GitStatus = candidate.GitStatus! with { ChangedFiles = ["src/b.cs", "src/a.cs"] } };
                break;

            default:
                throw new InvalidOperationException($"Unknown confirmation cell '{cell}'.");
        }

        if (cell != "absent")
            SeedReceiptRow(TaskId, GoalId, canonical, SeededFirstStoredText);

        if (cell is not ("absent" or "task-case"))
        {
            // THE VECTOR REALLY DIFFERS: a receipt carrying this vector's worker and result encodes to
            // DIFFERENT canonical evidence than the retained one, so a false answer cannot be an
            // accident of a vector that accidentally rebuilt the retained evidence.
            var other = CompletionReceiptCodec.Encode(
                new CompletionReceipt(GoalId, workerId, WorkerRole.Coder, StoredSlot(), candidate));
            Assert.NotEqual(canonical, other);
        }

        if (cell is "metrics-issues-reordered" or "git-changed-files-reordered")
        {
            // THE VECTOR IS REORDER-ONLY, proven rather than asserted in prose: the candidate's list
            // is a genuine PERMUTATION of the retained one — identical membership and identical
            // length — so an order-INSENSITIVE comparison could not distinguish it, and only the
            // ORDER differs.
            var retainedResult = MappedResult();
            var retainedList = cell == "metrics-issues-reordered"
                ? retainedResult.Metrics!.Issues
                : retainedResult.GitStatus!.ChangedFiles;
            var candidateList = cell == "metrics-issues-reordered"
                ? candidate.Metrics!.Issues
                : candidate.GitStatus!.ChangedFiles;

            Assert.Equal(retainedList.Count, candidateList.Count);
            Assert.Equal(
                retainedList.Order(StringComparer.Ordinal),
                candidateList.Order(StringComparer.Ordinal));
            Assert.NotEqual(retainedList, candidateList); // …but the ORDER really differs
        }

        if (cell == "task-case")
            Assert.Null(RawReceiptPayload(taskId)); // the case-variant key genuinely finds no row

        var recorder = NewRecorder(factory);
        Assert.False(recorder.ConfirmStoredReceipt(workerId, taskId, candidate));

        if (cell == "absent")
        {
            Assert.Equal(0L, ReceiptRowCount(TaskId));
        }
        else
        {
            Assert.Equal(1L, ReceiptRowCount(TaskId));
            Assert.Equal(canonical, RawReceiptPayload(TaskId));
            Assert.Equal(SeededFirstStoredText, RawFirstStoredText(TaskId));
        }
    }

    /// <summary>
    /// NULL IS NOT EMPTY, IN BOTH DIRECTIONS, THROUGH THE CONFIRMATION ITSELF. A retained
    /// <c>null</c> iteration-start SHA does NOT match a candidate carrying the EMPTY STRING, and a
    /// retained EMPTY STRING does not match a candidate carrying <c>null</c>. Both answer
    /// <c>false</c> and leave the retained row byte-identical.
    /// </summary>
    /// <remarks>
    /// DISCRIMINATING: the existing codec distinguishes these two states (a JSON <c>null</c> versus
    /// an empty JSON string), which each cell PROVES by comparing canonical texts and by asserting
    /// the retained payload's own literal token. A confirmation that collapsed "absent" and "empty"
    /// — in the NEW operation only, where no other suite would notice — would answer <c>true</c> here
    /// and fail.
    /// </remarks>
    [Theory]
    [InlineData("retained-null-candidate-empty")]
    [InlineData("retained-empty-candidate-null")]
    public void ConfirmStoredReceipt_NullVersusEmptyIterationStartSha_ReturnsFalseAndChangesNothing(string cell)
    {
        var factory = NewFactory();

        (string? retainedSha, string? candidateSha, string retainedToken) = cell switch
        {
            "retained-null-candidate-empty" => ((string?)null, (string?)"", "\"iterationStartSha\":null"),
            "retained-empty-candidate-null" => ((string?)"", (string?)null, "\"iterationStartSha\":\"\""),
            _ => throw new InvalidOperationException($"Unknown iteration-start-SHA cell '{cell}'."),
        };

        var canonical = SeedRetainedReceiptFor(MappedResult() with { IterationStartSha = retainedSha });

        // THE RETAINED EVIDENCE REALLY CARRIES THE STATE THIS CELL NAMES — a JSON null or an empty
        // JSON string — so the two cells are genuinely different setups, not the same one twice.
        Assert.Contains(retainedToken, RawReceiptPayload(TaskId)!, StringComparison.Ordinal);

        // A SEPARATELY ALLOCATED candidate carrying the OTHER state.
        var candidate = MappedResult() with { IterationStartSha = candidateSha };

        // THE CODEC ITSELF DISTINGUISHES THEM: the two canonical texts are not equal.
        Assert.NotEqual(canonical, CanonicalFor(candidate));

        Assert.False(NewRecorder(factory).ConfirmStoredReceipt(WorkerId, TaskId, candidate));

        Assert.Equal(1L, ReceiptRowCount(TaskId));
        Assert.Equal(canonical, RawReceiptPayload(TaskId));
        Assert.Equal(SeededFirstStoredText, RawFirstStoredText(TaskId));
    }

    /// <summary>
    /// AN EMPTY OR WHITESPACE MODEL CONFIRMS VERBATIM THROUGH THE READ-ONLY PATH: when the retained
    /// evidence and the supplied completion carry the SAME empty, whitespace-only or
    /// trailing-whitespace model, the confirmation answers <c>true</c> — with no trimming, no
    /// normalization and no queue/assignment-model fallback (there is no assignment row at all).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("copilot/whitespace  ")]
    public void ConfirmStoredReceipt_MatchingEmptyOrWhitespaceModel_ConfirmsVerbatim(string model)
    {
        var factory = NewFactory();
        var canonical = SeedRetainedReceiptFor(MappedResult(model));

        // THE RETAINED PAYLOAD CARRIES THE MODEL VERBATIM — untrimmed and unsubstituted.
        Assert.Contains($"\"model\":\"{model}\"", RawReceiptPayload(TaskId)!, StringComparison.Ordinal);
        // …and there is NO assignment row a fallback could have come from.
        Assert.Equal(0L, AssignmentRowCount);

        // A SEPARATELY ALLOCATED equivalent completion carrying the SAME model confirms.
        Assert.True(NewRecorder(factory).ConfirmStoredReceipt(WorkerId, TaskId, MappedResult(model)));

        Assert.Equal(1L, ReceiptRowCount(TaskId));
        Assert.Equal(canonical, RawReceiptPayload(TaskId));
        Assert.Equal(SeededFirstStoredText, RawFirstStoredText(TaskId));
        Assert.Equal(0L, AssignmentRowCount);
    }

    /// <summary>
    /// EMPTY IS NOT WHITESPACE AND NEITHER IS A WILDCARD: a model that differs ONLY by
    /// empty-versus-whitespace (in either direction), by whitespace LENGTH, or by
    /// empty-versus-nonempty answers <c>false</c>. The retained evidence is kept verbatim.
    /// </summary>
    /// <remarks>
    /// DISCRIMINATING: a confirmation that trimmed the model, or that treated a blank model as
    /// "unknown, therefore matching", would answer <c>true</c> for every cell here. Each cell first
    /// proves the codec's own canonical texts differ.
    /// </remarks>
    [Theory]
    [InlineData("", "   ")]
    [InlineData("   ", "")]
    [InlineData("   ", "  ")]
    [InlineData("", "selected-model")]
    [InlineData("selected-model", "")]
    [InlineData("copilot/whitespace  ", "copilot/whitespace")]
    public void ConfirmStoredReceipt_ModelDiffersOnlyByBlankness_ReturnsFalseAndKeepsEvidenceVerbatim(
        string retainedModel, string candidateModel)
    {
        var factory = NewFactory();
        var canonical = SeedRetainedReceiptFor(MappedResult(retainedModel));

        Assert.Contains($"\"model\":\"{retainedModel}\"", RawReceiptPayload(TaskId)!, StringComparison.Ordinal);

        var candidate = MappedResult(candidateModel);

        // THE CODEC ITSELF DISTINGUISHES THEM.
        Assert.NotEqual(canonical, CanonicalFor(candidate));

        Assert.False(NewRecorder(factory).ConfirmStoredReceipt(WorkerId, TaskId, candidate));

        Assert.Equal(1L, ReceiptRowCount(TaskId));
        Assert.Equal(canonical, RawReceiptPayload(TaskId));
        Assert.Equal(SeededFirstStoredText, RawFirstStoredText(TaskId));
    }

    /// <summary>
    /// A NULL RESULT IS A CALLER BUG, refused BEFORE anything else exists: the argument-null refusal
    /// names <c>result</c>, no context is acquired and no row is touched.
    /// </summary>
    [Fact]
    public void ConfirmStoredReceipt_NullResult_ThrowsArgumentNullAndAcquiresNothing()
    {
        var creates = 0;
        var factory = NewFactory();
        factory.OnCreate = () => creates++;

        var thrown = Assert.Throws<ArgumentNullException>(
            () => NewRecorder(factory).ConfirmStoredReceipt(WorkerId, TaskId, null!));

        Assert.Equal("result", thrown.ParamName);
        Assert.Equal(0, creates);
        Assert.Equal(0L, ReceiptRowCount(TaskId));
    }

    /// <summary>
    /// A MISSING OR BLANK IDENTITY — the pinned worker, the confirmation's task id, or the result's own
    /// task id — and an ORDINAL RESULT-versus-TASK disagreement are all
    /// <see cref="WorkerCompletionRecordingFailureReason.InvalidContext"/> refusals with no store
    /// status and no inner exception, decided BEFORE any store access: no context is acquired and
    /// nothing is written.
    /// </summary>
    [Theory]
    [InlineData("null-worker", null, TaskId, TaskId)]
    [InlineData("blank-worker", "   ", TaskId, TaskId)]
    [InlineData("null-task", WorkerId, null, TaskId)]
    [InlineData("blank-task", WorkerId, "  ", TaskId)]
    [InlineData("null-result-task", WorkerId, TaskId, null)]
    [InlineData("blank-result-task", WorkerId, TaskId, "")]
    [InlineData("result-task-disagrees", WorkerId, TaskId, "some-other-task")]
    public void ConfirmStoredReceipt_MissingIdentityOrDisagreement_RefusesInvalidContextAndAcquiresNothing(
        string cell, string? workerId, string? taskId, string? resultTaskId)
    {
        var creates = 0;
        var factory = NewFactory();
        factory.OnCreate = () => creates++;

        var candidate = MappedResult() with { TaskId = resultTaskId! };

        var refusal = Assert.Throws<WorkerCompletionRecordingException>(
            () => NewRecorder(factory).ConfirmStoredReceipt(workerId!, taskId!, candidate));

        Assert.Equal(WorkerCompletionRecordingFailureReason.InvalidContext, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.Null(refusal.InnerException);
        Assert.False(string.IsNullOrWhiteSpace(refusal.Message), cell);
        Assert.Equal(0, creates);
        Assert.Equal(0L, ReceiptRowCount(TaskId));
    }

    /// <summary>
    /// AN UNUSABLE RETAINED ROW — a corrupt or unsupported payload, a decodable payload whose task or
    /// goal identity disagrees with its row, and an unmaterializable first-stored timestamp — is
    /// ALWAYS <see cref="WorkerCompletionRecordingFailureReason.StoreError"/> with the EXACT caught
    /// exception. It is never reported as absence (<c>false</c>), never as a difference
    /// (<c>false</c>), and never as a fabricated <c>Conflict</c>/<c>Indeterminate</c>.
    /// </summary>
    [Theory]
    [InlineData("corrupt-payload")]
    [InlineData("unsupported-version")]
    [InlineData("row-payload-task-mismatch")]
    [InlineData("row-payload-goal-mismatch")]
    [InlineData("bad-timestamp")]
    public void ConfirmStoredReceipt_UnusableRetainedRow_IsStoreErrorNeverAbsenceOrMismatch(string cell)
    {
        var factory = NewFactory();
        var canonical = CompletionReceiptCodec.Encode(RetainedReceipt());
        var seededPayload = canonical;
        var seededTime = SeededFirstStoredText;

        switch (cell)
        {
            case "corrupt-payload":
                seededPayload = """{"version":1}""";
                break;

            case "unsupported-version":
                seededPayload = """{"version":2}""";
                break;

            case "row-payload-task-mismatch":
                seededPayload = CompletionReceiptCodec.Encode(
                    new CompletionReceipt(GoalId, WorkerId, WorkerRole.Coder, StoredSlot("task-in-payload"), MappedResultWithTaskId("task-in-payload")));
                break;

            case "row-payload-goal-mismatch":
                // The payload is the SAME receipt; only the ROW's goal column disagrees.
                break;

            case "bad-timestamp":
                seededTime = "not-a-timestamp";
                break;

            default:
                throw new InvalidOperationException($"Unknown retained-row cell '{cell}'.");
        }

        SeedReceiptRow(
            TaskId,
            cell == "row-payload-goal-mismatch" ? "goal-somewhere-else" : GoalId,
            seededPayload,
            seededTime);

        var refusal = Assert.Throws<WorkerCompletionRecordingException>(
            () => NewRecorder(factory).ConfirmStoredReceipt(WorkerId, TaskId, MappedResult()));

        Assert.Equal(WorkerCompletionRecordingFailureReason.StoreError, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.NotNull(refusal.InnerException);

        switch (cell)
        {
            case "corrupt-payload":
            case "unsupported-version":
                Assert.IsType<CompletionReceiptCodecException>(refusal.InnerException);
                break;

            case "row-payload-task-mismatch":
            case "row-payload-goal-mismatch":
                Assert.IsType<InvalidOperationException>(refusal.InnerException);
                Assert.Contains("disagree", refusal.InnerException!.Message, StringComparison.Ordinal);
                break;

            case "bad-timestamp":
                // The failure is the TIMESTAMP materialization, not a payload refusal.
                Assert.IsNotType<CompletionReceiptCodecException>(refusal.InnerException);
                break;
        }

        // THE ROW IS UNTOUCHED — a refusal never repairs, replaces or deletes anything.
        Assert.Equal(1L, ReceiptRowCount(TaskId));
        Assert.Equal(seededPayload, RawReceiptPayload(TaskId));
        Assert.Equal(seededTime, RawFirstStoredText(TaskId));
    }

    /// <summary>
    /// A CONTEXT-ACQUISITION FAILURE is a <c>StoreError</c> retaining the EXACT sentinel instance —
    /// the shared fixture factory from the store fixture makes the acquisition itself fail, so no
    /// fabricated or reconstructed wrapper can satisfy the identity assertion.
    /// </summary>
    [Fact]
    public void ConfirmStoredReceipt_ContextAcquisitionThrows_StoreErrorWithExactCause()
    {
        var sentinel = new InvalidOperationException("confirmation acquisition SENTINEL");
        var recorder = NewRecorder(new CompletionReceiptStoreTests.ReceiptThrowingContextFactory(sentinel));

        var refusal = Assert.Throws<WorkerCompletionRecordingException>(
            () => recorder.ConfirmStoredReceipt(WorkerId, TaskId, MappedResult()));

        Assert.Equal(WorkerCompletionRecordingFailureReason.StoreError, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.Same(sentinel, refusal.InnerException);
    }

    /// <summary>
    /// A FAILING READ QUERY is a <c>StoreError</c> retaining the EXACT sentinel the interceptor threw,
    /// and the injection really fired. A read fault is never dressed up as absence or as a difference.
    /// </summary>
    [Fact]
    public void ConfirmStoredReceipt_ReadQueryThrows_StoreErrorWithExactCause()
    {
        var interceptor = new ReceiptSelectThrowingInterceptor();
        var factory = NewFactory(interceptor);
        SeedReceiptRow(TaskId, GoalId, CompletionReceiptCodec.Encode(RetainedReceipt()), SeededFirstStoredText);

        var refusal = Assert.Throws<WorkerCompletionRecordingException>(
            () => NewRecorder(factory).ConfirmStoredReceipt(WorkerId, TaskId, MappedResult()));

        Assert.Equal(WorkerCompletionRecordingFailureReason.StoreError, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.Same(interceptor.Sentinel, refusal.InnerException);
        Assert.Equal(1, interceptor.ThrowCount);
        Assert.Equal(1L, ReceiptRowCount(TaskId));
    }

    /// <summary>
    /// CODEC-INVALID CANDIDATE EVIDENCE FAILS CLOSED WITH THE PRESCRIBED CLASSIFICATION: an
    /// unrepresentable supplied result (a null output, a null model, an unpaired UTF-16 surrogate)
    /// cannot be encoded into canonical evidence, so the operation reports
    /// <see cref="WorkerCompletionRecordingFailureReason.StoreError"/> carrying the codec's own
    /// exception — never a silent "no match" and never a fabricated store status. The retained row is
    /// untouched.
    /// </summary>
    [Theory]
    [InlineData("null-output")]
    [InlineData("null-model")]
    [InlineData("unpaired-surrogate")]
    public void ConfirmStoredReceipt_UnrepresentableCandidateEvidence_StoreErrorWithCodecCause(string kind)
    {
        var factory = NewFactory();
        var canonical = CompletionReceiptCodec.Encode(RetainedReceipt());
        SeedReceiptRow(TaskId, GoalId, canonical, SeededFirstStoredText);

        var candidate = kind switch
        {
            "null-output" => MappedResult() with { Output = null! },
            "null-model" => MappedResult() with { Model = null! },
            "unpaired-surrogate" => MappedResult() with { Output = "bad-\uD800-tail" },
            _ => throw new InvalidOperationException($"Unknown candidate vector '{kind}'."),
        };

        var refusal = Assert.Throws<WorkerCompletionRecordingException>(
            () => NewRecorder(factory).ConfirmStoredReceipt(WorkerId, TaskId, candidate));

        Assert.Equal(WorkerCompletionRecordingFailureReason.StoreError, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.IsType<CompletionReceiptCodecException>(refusal.InnerException);

        Assert.Equal(1L, ReceiptRowCount(TaskId));
        Assert.Equal(canonical, RawReceiptPayload(TaskId));
        Assert.Equal(SeededFirstStoredText, RawFirstStoredText(TaskId));
    }

    /// <summary>
    /// THE CONFIRMATION ISSUES NO WRITE COMMAND AT ALL — not an INSERT, an UPDATE or a DELETE, and not
    /// even a no-op insert — and leaves the row byte-identical. The ONLY statement the whole
    /// operation produces, once per confirmation, is a READ of the retained receipt.
    /// </summary>
    [Fact]
    public void ConfirmStoredReceipt_IssuesOnlyReadsAndLeavesTheRowByteIdentical()
    {
        var capture = new ReceiptCommandCaptureInterceptor();
        var factory = NewFactory(capture);
        var canonical = CompletionReceiptCodec.Encode(RetainedReceipt());
        SeedReceiptRow(TaskId, GoalId, canonical, SeededFirstStoredText);

        var recorder = NewRecorder(factory);
        Assert.True(recorder.ConfirmStoredReceipt(WorkerId, TaskId, MappedResult()));
        Assert.True(recorder.ConfirmStoredReceipt(WorkerId, TaskId, MappedResult()));

        // EXACTLY ONE COMMAND PER CONFIRMATION, AND IT IS A READ.
        Assert.Equal(2, capture.Commands.Count);
        Assert.All(capture.Commands, command => Assert.Equal("Reader", command.Kind));
        Assert.All(capture.Commands, command => Assert.Contains(
            "completion_receipts", command.Sql, StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(capture.Commands, c => c.Sql.Contains("INSERT", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(capture.Commands, c => c.Sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(capture.Commands, c => c.Sql.Contains("DELETE", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1L, ReceiptRowCount(TaskId));
        Assert.Equal(canonical, RawReceiptPayload(TaskId));
        Assert.Equal(SeededFirstStoredText, RawFirstStoredText(TaskId));
    }

    /// <summary>
    /// THE READ-ONLY CONFIRMATION THROUGH THE PRODUCTION CONTAINER: the container's own recorder
    /// retains a receipt on the container's own graph, then the container's own
    /// <see cref="IWorkerCompletionRecorder"/> confirms an equivalent completion and rejects a
    /// differing one — with the retained row unchanged in both cases.
    /// </summary>
    [Fact]
    public void DiRecorder_ConfirmStoredReceipt_ReadsTheContainerGraphsOwnRetainedReceipt()
    {
        using var factory = new HiveTestFactory();
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;

        var assignmentStore = provider.GetRequiredService<WorkerAssignmentContextStore>();
        var receiptStore = provider.GetRequiredService<CompletionReceiptStore>();
        var recorder = provider.GetRequiredService<IWorkerCompletionRecorder>();

        const string taskId = "task-di-confirmation";
        var slot = new WorkSlot(taskId, new WorkSlotPosition(1, GoalPhase.Coding, 1), 1);
        var context = new WorkerAssignmentContext("goal-di-confirm", "worker-di-confirm", WorkerRole.Coder, slot, "assigned-model");
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, assignmentStore.InsertOnce(context).Status);

        var task = new CopilotHive.Services.WorkTask
        {
            TaskId = taskId,
            GoalId = "goal-di-confirm",
            GoalDescription = "confirm through the container",
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

        recorder.Record("worker-di-confirm", task, result);

        // THE PRE-CONFIRMATION BASELINE, read through the CONTAINER'S OWN store (the raw helpers above
        // read this fixture's own database, not the container's).
        var beforeConfirm = receiptStore.Load(taskId);
        Assert.NotNull(beforeConfirm);
        var payloadBefore = CompletionReceiptCodec.Encode(beforeConfirm!.Receipt);
        var firstStoredBefore = beforeConfirm.FirstStoredAtUtc;
        Assert.Equal("worker-di-confirm", beforeConfirm.Receipt.WorkerId);

        // A SEPARATELY ALLOCATED EQUIVALENT COMPLETION CONFIRMS…
        var equivalent = new CopilotHive.Services.TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "container output",
            Model = "selected-model",
        };
        Assert.True(recorder.ConfirmStoredReceipt("worker-di-confirm", taskId, equivalent));

        // …AND A DIFFERING ONE DOES NOT — so the container's recorder really reads the retained row
        // rather than falling back to the record-only default.
        Assert.False(recorder.ConfirmStoredReceipt(
            "worker-di-confirm", taskId, equivalent with { Output = "a-different-output" }));

        // THE ROW IS UNCHANGED: same canonical payload, same first-stored instant, one row.
        var afterConfirm = receiptStore.Load(taskId);
        Assert.NotNull(afterConfirm);
        Assert.Equal(payloadBefore, CompletionReceiptCodec.Encode(afterConfirm!.Receipt));
        Assert.Equal(firstStoredBefore, afterConfirm.FirstStoredAtUtc);
    }

    /// <summary>The receipt the retained-evidence vectors seed and compare against.</summary>
    private static CompletionReceipt RetainedReceipt() =>
        new(GoalId, WorkerId, WorkerRole.Coder, StoredSlot(), MappedResult());

    /// <summary>
    /// The canonical text of the receipt a given mapped result would produce under the RETAINED
    /// identity (the stored goal/worker/role and the FULL stored slot) — the single place the
    /// edge-value vectors prove their own discrimination from, through the codec itself.
    /// </summary>
    private static string CanonicalFor(CopilotHive.Services.TaskResult result) =>
        CompletionReceiptCodec.Encode(
            new CompletionReceipt(GoalId, WorkerId, WorkerRole.Coder, StoredSlot(), result));

    /// <summary>
    /// Seeds the retained receipt row DIRECTLY for a given mapped result (no assignment row, so a
    /// queue/assignment-model fallback has nothing to fall back to) and returns its canonical text.
    /// </summary>
    private string SeedRetainedReceiptFor(CopilotHive.Services.TaskResult result)
    {
        var canonical = CanonicalFor(result);
        SeedReceiptRow(TaskId, GoalId, canonical, SeededFirstStoredText);
        return canonical;
    }

    /// <summary>The same rich mapped result, but naming a different task id (the row/payload mismatch vector).</summary>
    private static CopilotHive.Services.TaskResult MappedResultWithTaskId(string taskId) =>
        MappedResult() with { TaskId = taskId };

    /// <summary>
    /// A RECORD-ONLY implementation of the interface: it implements ONLY the recording operation, so it
    /// compiles purely because <see cref="IWorkerCompletionRecorder.ConfirmStoredReceipt"/> has a
    /// default body. It proves the fail-closed default and that no recording is involved.
    /// </summary>
    private sealed class RecordOnlyRecorder : IWorkerCompletionRecorder
    {
        private int _recordCount;

        /// <summary>How many times <see cref="Record"/> was called — must stay zero.</summary>
        public int RecordCount => Volatile.Read(ref _recordCount);

        /// <inheritdoc />
        public void Record(string workerId, WorkTask task, TaskResult result) =>
            Interlocked.Increment(ref _recordCount);
    }

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
