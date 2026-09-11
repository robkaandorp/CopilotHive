using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

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
/// The completion-receipt STORE, end to end against a REAL file-backed SQLite database and focused
/// fakes: the INSERT-only write contract (<c>Stored</c>/<c>AlreadyStored</c>/<c>Conflict</c>/
/// <c>Indeterminate</c>), the frozen canonical payload, the ordinal duplicate comparison, the
/// read-error vs write-uncertainty separation, the guarded cleanup, and the two-connection race.
/// </summary>
/// <remarks>
/// <para>
/// EVERY FIXTURE IS ISOLATED: the database is a per-instance temporary FILE and every context is
/// created from a connection STRING with <c>Pooling=False</c>, so EF owns and closes its own
/// connection and the file is deletable afterwards. A readback is therefore a genuine fresh open of
/// the file, never a read through a handle the test left dangling.
/// </para>
/// <para>
/// THE INJECTIONS ARE GENUINE EF INTERCEPTORS AND REAL DOMAIN FAILURES — a command interceptor that
/// throws a pre-created sentinel at a specific interception point, an affected-row-count override at
/// the provider boundary, a throwing logger, and a disposal seam that really disposes and then
/// fails — never a fabricated token. Raw assertions read the table through their own connection,
/// bypassing EF's change tracker entirely.
/// </para>
/// </remarks>
public sealed class CompletionReceiptStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"copilothive-receipt-store-{Guid.NewGuid():N}.db");

    private readonly List<IDisposable> _fixtures = [];

    /// <summary>The one instant every fixed-clock fixture stores, so the text is predictable.</summary>
    private static readonly DateTimeOffset FixedNow =
        new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero).AddTicks(1234567);

    private static readonly string FixedNowText =
        FixedNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public CompletionReceiptStoreTests()
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

    private TestContextFactory NewFactory(params IInterceptor[] interceptors)
    {
        var factory = new TestContextFactory(ConnectionString, interceptors);
        _fixtures.Add(factory);
        return factory;
    }

    private CompletionReceiptStore NewStore(
        IDbContextFactory<CopilotHiveDbContext> factory,
        TimeProvider? timeProvider = null,
        ILogger<CompletionReceiptStore>? logger = null) =>
        new(factory, logger ?? NullLogger<CompletionReceiptStore>.Instance, timeProvider);

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

    private void SeedReceiptRow(string taskId, string goalId, string payloadJson, string firstStoredText) =>
        ExecuteRaw(
            "INSERT INTO completion_receipts (task_id, goal_id, payload_json, first_stored_at_utc) " +
            $"VALUES ('{Escape(taskId)}', '{Escape(goalId)}', '{Escape(payloadJson)}', '{Escape(firstStoredText)}')");

    private string? RawPayload(string taskId) =>
        (string?)RawScalar($"SELECT payload_json FROM completion_receipts WHERE task_id = '{Escape(taskId)}'");

    private string? RawFirstStoredText(string taskId) =>
        (string?)RawScalar($"SELECT first_stored_at_utc FROM completion_receipts WHERE task_id = '{Escape(taskId)}'");

    private long ReceiptRowCount(string taskId) =>
        (long)RawScalar($"SELECT COUNT(*) FROM completion_receipts WHERE task_id = '{Escape(taskId)}'")!;

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

    // ───────────────────────────── receipt builders ─────────────────────────────

    private static CompletionReceipt Receipt(
        string goalId = "goal-base",
        string workerId = "worker-base",
        string taskId = "task-base",
        GoalPhase phase = GoalPhase.Coding,
        WorkerRole role = WorkerRole.Coder,
        int iteration = 1,
        int occurrence = 1,
        int attempt = 1,
        TaskOutcome status = TaskOutcome.Completed,
        string output = "out-base",
        string model = "model-base",
        string? sha = "sha-base",
        TaskMetrics? metrics = null,
        GitChangeSummary? gitStatus = null) =>
        new(
            goalId,
            workerId,
            role,
            new WorkSlot(taskId, new WorkSlotPosition(iteration, phase, occurrence), attempt),
            new TaskResult
            {
                TaskId = taskId,
                Status = status,
                Output = output,
                Model = model,
                IterationStartSha = sha,
                Metrics = metrics,
                GitStatus = gitStatus,
            });

    private static TaskMetrics Metrics(
        string verdict = "PASS",
        bool buildSuccess = true,
        int totalTests = 10,
        int passedTests = 9,
        int failedTests = 1,
        double coverage = 87.5,
        List<string>? issues = null,
        string summary = "summary-base") =>
        new()
        {
            Verdict = verdict,
            BuildSuccess = buildSuccess,
            TotalTests = totalTests,
            PassedTests = passedTests,
            FailedTests = failedTests,
            CoveragePercent = coverage,
            Issues = issues ?? ["issue-1", "issue-2"],
            Summary = summary,
        };

    private static GitChangeSummary Git(
        int filesChanged = 2,
        int insertions = 10,
        int deletions = 1,
        bool pushed = true,
        List<string>? changedFiles = null) =>
        new()
        {
            FilesChanged = filesChanged,
            Insertions = insertions,
            Deletions = deletions,
            Pushed = pushed,
            ChangedFiles = changedFiles ?? ["src/a.cs", "src/b.cs"],
        };

    /// <summary>
    /// The representative receipt shapes the round-trip matrix covers: the null-optional form, the
    /// full form, every canonical non-finite coverage token, empty strings, non-BMP text (a
    /// well-formed surrogate pair), every <see cref="TaskOutcome"/>, and the remaining worker-backed
    /// phase/role pairs.
    /// </summary>
    public static TheoryData<string> ReceiptVariants => new()
    {
        "null-optionals",
        "full",
        "nan-coverage",
        "positive-infinity-coverage",
        "negative-infinity-coverage",
        "empty-strings",
        "non-bmp-text",
        "failed",
        "cancelled",
        "testing-phase",
        "review-phase",
        "docwriting-phase",
        "improve-phase",
        "multi-iteration",
    };

    private static CompletionReceipt Variant(string kind, string taskId) => kind switch
    {
        "null-optionals" => Receipt(taskId: taskId),
        "full" => Receipt(taskId: taskId, metrics: Metrics(), gitStatus: Git(), sha: "abc123"),
        "nan-coverage" => Receipt(taskId: taskId, metrics: Metrics(coverage: double.NaN), gitStatus: Git()),
        "positive-infinity-coverage" => Receipt(
            taskId: taskId, metrics: Metrics(coverage: double.PositiveInfinity), gitStatus: Git()),
        "negative-infinity-coverage" => Receipt(
            taskId: taskId, metrics: Metrics(coverage: double.NegativeInfinity), gitStatus: Git()),
        "empty-strings" => Receipt(
            taskId: taskId,
            output: "",
            model: "",
            sha: "",
            metrics: Metrics(verdict: "", summary: "", issues: []),
            gitStatus: Git(changedFiles: [])),
        "non-bmp-text" => Receipt(
            taskId: taskId,
            output: "out-\U0001F600-\U0001F389",
            model: "\U0001F680-model",
            metrics: Metrics(summary: "summary-\U0001F600", issues: ["issue-\U0001F389"]),
            gitStatus: Git(changedFiles: ["src/\U0001F600.cs"])),
        "failed" => Receipt(
            taskId: taskId, status: TaskOutcome.Failed, metrics: Metrics(buildSuccess: false, failedTests: 4)),
        "cancelled" => Receipt(
            taskId: taskId, status: TaskOutcome.Cancelled, metrics: Metrics(coverage: double.NaN)),
        "testing-phase" => Receipt(taskId: taskId, phase: GoalPhase.Testing, role: WorkerRole.Tester),
        "review-phase" => Receipt(taskId: taskId, phase: GoalPhase.Review, role: WorkerRole.Reviewer),
        "docwriting-phase" => Receipt(taskId: taskId, phase: GoalPhase.DocWriting, role: WorkerRole.DocWriter),
        "improve-phase" => Receipt(taskId: taskId, phase: GoalPhase.Improve, role: WorkerRole.Improver),
        "multi-iteration" => Receipt(
            taskId: taskId, iteration: 7, occurrence: 3, attempt: 5, metrics: Metrics(), gitStatus: Git()),
        _ => throw new InvalidOperationException($"Unknown receipt variant '{kind}'."),
    };

    // ═══════════════════════════════════════════════════════════════════════
    // (1) Round trip across a genuine close-and-reopen of the database file
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FULL ROUND TRIP, for every representative receipt shape: insert through the store, dispose
    /// EVERY fixture (factory, store, context), then load through a FRESH factory over the same file.
    /// The complete codec data survives — proven by re-encoding the loaded receipt and comparing the
    /// canonical text ordinally — together with the EXACT first-stored UTC ticks and text.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: drop any member from the encode (or from the stored payload) and the canonical
    /// text comparison fails; substitute a defaulted/rounded timestamp and both the ticks assertion
    /// and the raw-text assertion fail.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ReceiptVariants))]
    public void InsertOnce_ThenFreshFactoryLoad_PreservesCompleteCodecDataAndExactFirstStoredTime(string kind)
    {
        var taskId = $"task-variant-{kind}";
        var receipt = Variant(kind, taskId);
        var canonical = CompletionReceiptCodec.Encode(receipt);

        var writerFactory = NewFactory();
        var writer = NewStore(writerFactory, new FixedTimeProvider(FixedNow));

        var writeResult = writer.InsertOnce(receipt);

        Assert.Equal(CompletionReceiptWriteStatus.Stored, writeResult.Status);
        Assert.Null(writeResult.WriteException);

        // The row is durable ON DISK and its payload is the canonical text, byte for byte.
        Assert.Equal(canonical, RawPayload(taskId));
        Assert.Equal(FixedNowText, RawFirstStoredText(taskId));

        // DISPOSE EVERY FIXTURE before reading anything back.
        writerFactory.Dispose();

        var readerFactory = NewFactory();
        var reader = NewStore(readerFactory, new FixedTimeProvider(FixedNow));

        var loaded = reader.Load(taskId);

        Assert.NotNull(loaded);
        // THE COMPLETE CODEC DATA: the canonical text of the loaded receipt equals the candidate's.
        Assert.Equal(canonical, CompletionReceiptCodec.Encode(loaded!.Receipt), StringComparer.Ordinal);
        Assert.Equal(receipt.GoalId, loaded.Receipt.GoalId);
        Assert.Equal(receipt.WorkerId, loaded.Receipt.WorkerId);
        Assert.Equal(receipt.Role, loaded.Receipt.Role);
        Assert.Equal(receipt.Result.Status, loaded.Receipt.Result.Status);

        // THE EXACT FIRST-STORED INSTANT: ticks, kind and the raw column text all agree.
        Assert.Equal(FixedNow.UtcDateTime.Ticks, loaded.FirstStoredAtUtc.Ticks);
        Assert.Equal(DateTimeKind.Utc, loaded.FirstStoredAtUtc.Kind);
        Assert.Equal(FixedNow.UtcDateTime, loaded.FirstStoredAtUtc);
        Assert.Equal(FixedNowText, loaded.FirstStoredAtUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// THE LONG TAIL OF THE FULL SHAPE, asserted member by member (not merely via the canonical
    /// text): every nested metrics and git-status value survives the round trip, including the empty
    /// lists and the null-able members, and the two evidence lists come back as FRESH lists.
    /// </summary>
    [Fact]
    public void InsertOnce_FullReceipt_Load_ReturnsEveryNestedValueAndFreshLists()
    {
        var receipt = Receipt(
            goalId: "goal-rich",
            workerId: "worker-rich",
            taskId: "task-rich",
            metrics: Metrics(verdict: "APPROVE", buildSuccess: true, totalTests: 42, passedTests: 41,
                failedTests: 1, coverage: 91.25, issues: ["issue-a", "issue-b"], summary: "summary-rich"),
            gitStatus: Git(filesChanged: 3, insertions: 99, deletions: 7, pushed: false,
                changedFiles: ["src/x.cs", "src/y.cs", "src/z.cs"]),
            sha: "deadbeef");

        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));
        Assert.Equal(CompletionReceiptWriteStatus.Stored, store.InsertOnce(receipt).Status);

        var loaded = store.Load("task-rich");

        Assert.NotNull(loaded);
        var metrics = loaded!.Receipt.Result.Metrics;
        Assert.NotNull(metrics);
        Assert.Equal("APPROVE", metrics!.Verdict);
        Assert.True(metrics.BuildSuccess);
        Assert.Equal(42, metrics.TotalTests);
        Assert.Equal(41, metrics.PassedTests);
        Assert.Equal(1, metrics.FailedTests);
        Assert.Equal(91.25, metrics.CoveragePercent);
        Assert.Equal(["issue-a", "issue-b"], metrics.Issues);
        Assert.Equal("summary-rich", metrics.Summary);

        var git = loaded.Receipt.Result.GitStatus;
        Assert.NotNull(git);
        Assert.Equal(3, git!.FilesChanged);
        Assert.Equal(99, git.Insertions);
        Assert.Equal(7, git.Deletions);
        Assert.False(git.Pushed);
        Assert.Equal(["src/x.cs", "src/y.cs", "src/z.cs"], git.ChangedFiles);

        Assert.Equal("deadbeef", loaded.Receipt.Result.IterationStartSha);
        Assert.Equal(GoalPhase.Coding, loaded.Receipt.Slot.Position!.Phase);
        Assert.Equal(1, loaded.Receipt.Slot.Position.Iteration);
        Assert.Equal(1, loaded.Receipt.Slot.Position.Occurrence);
        Assert.Equal(1, loaded.Receipt.Slot.Attempt);

        // THE LISTS ARE FRESH: they are not the caller's instances, and not the stored row's.
        Assert.NotSame(receipt.Result.Metrics!.Issues, metrics.Issues);
        Assert.NotSame(receipt.Result.GitStatus!.ChangedFiles, git.ChangedFiles);
    }

    /// <summary>
    /// TWO LOADS ARE INDEPENDENT: each returns a DETACHED receipt, so mutating the lists of one
    /// cannot reach the other or any later load.
    /// </summary>
    [Fact]
    public void Load_Twice_ReturnsDetachedReceipts_MutatingOneDoesNotAffectTheNext()
    {
        var receipt = Receipt(taskId: "task-detach", metrics: Metrics(), gitStatus: Git());
        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));
        Assert.Equal(CompletionReceiptWriteStatus.Stored, store.InsertOnce(receipt).Status);

        var first = store.Load("task-detach");
        Assert.NotNull(first);
        Assert.NotSame(first, store.Load("task-detach"));

        // MUTATE the returned lists — the stored row must be unaffected.
        first!.Receipt.Result.Metrics!.Issues.Add("injected");
        first.Receipt.Result.Metrics.Issues.Clear();
        first.Receipt.Result.GitStatus!.ChangedFiles.Add("injected.cs");

        var second = store.Load("task-detach");
        Assert.NotNull(second);
        Assert.Equal(["issue-1", "issue-2"], second!.Receipt.Result.Metrics!.Issues);
        Assert.Equal(["src/a.cs", "src/b.cs"], second.Receipt.Result.GitStatus!.ChangedFiles);
        Assert.NotSame(first.Receipt.Result.Metrics.Issues, second.Receipt.Result.Metrics.Issues);

        // And the RAW row is unchanged too.
        Assert.Equal(CompletionReceiptCodec.Encode(receipt), RawPayload("task-detach"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) The duplicate and conflict truths
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AN IDENTICAL RETRY: the second insert matches the first row's canonical text ordinally, so it
    /// is <see cref="CompletionReceiptWriteStatus.AlreadyStored"/> — and the original raw payload and
    /// first-stored time are untouched (the retry's later clock is NOT written).
    /// </summary>
    [Fact]
    public void InsertOnce_IdenticalRetry_AlreadyStored_OriginalRowAndTimeUnchanged()
    {
        var receipt = Receipt(taskId: "task-identical", metrics: Metrics(), gitStatus: Git());
        var canonical = CompletionReceiptCodec.Encode(receipt);
        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        var first = store.InsertOnce(receipt);
        Assert.Equal(CompletionReceiptWriteStatus.Stored, first.Status);
        Assert.Equal(1L, ReceiptRowCount("task-identical"));

        // A LATER clock for the retry: if the retry wrote anything, the time would move.
        var retryStore = NewStore(factory, new FixedTimeProvider(FixedNow.AddHours(5)));
        var second = retryStore.InsertOnce(receipt);

        Assert.Equal(CompletionReceiptWriteStatus.AlreadyStored, second.Status);
        Assert.Null(second.WriteException);
        Assert.Equal(1L, ReceiptRowCount("task-identical"));
        Assert.Equal(canonical, RawPayload("task-identical"));
        Assert.Equal(FixedNowText, RawFirstStoredText("task-identical"));
    }

    /// <summary>
    /// THE CONFLICT MATRIX: a zero-row insert followed by a VALID but DIFFERENT row is
    /// <see cref="CompletionReceiptWriteStatus.Conflict"/> — including a different goal, worker,
    /// slot position or attempt, and every differing result field. Nothing is changed: the payload
    /// and the first-stored time stay byte-identical, and no second row appears.
    /// </summary>
    /// <remarks>
    /// Each vector asserts its own precondition (<c>Encode(mutated) != Encode(base)</c>), so a vector
    /// that accidentally rebuilt the base receipt cannot pass as a conflict.
    /// </remarks>
    [Theory]
    [InlineData("goal")]
    [InlineData("worker")]
    [InlineData("case-only-goal")]
    [InlineData("case-only-output")]
    [InlineData("iteration")]
    [InlineData("occurrence")]
    [InlineData("attempt")]
    [InlineData("phase-role")]
    [InlineData("status")]
    [InlineData("output")]
    [InlineData("model")]
    [InlineData("sha")]
    [InlineData("metrics-values")]
    [InlineData("metrics-null")]
    [InlineData("git-values")]
    [InlineData("git-null")]
    public void InsertOnce_DifferingReceipt_Conflict_OriginalRowAndTimeUnchanged(string kind)
    {
        const string taskId = "task-conflict";
        var stored = Receipt(taskId: taskId, metrics: Metrics(), gitStatus: Git());
        var storedCanonical = CompletionReceiptCodec.Encode(stored);

        var candidate = ConflictingCandidate(kind, taskId);
        var candidateCanonical = CompletionReceiptCodec.Encode(candidate);
        Assert.NotEqual(storedCanonical, candidateCanonical); // the vector really differs

        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));
        Assert.Equal(CompletionReceiptWriteStatus.Stored, store.InsertOnce(stored).Status);

        var retryStore = NewStore(factory, new FixedTimeProvider(FixedNow.AddDays(1)));
        var result = retryStore.InsertOnce(candidate);

        Assert.Equal(CompletionReceiptWriteStatus.Conflict, result.Status);
        Assert.Null(result.WriteException);
        Assert.Equal(1L, ReceiptRowCount(taskId));
        Assert.Equal(storedCanonical, RawPayload(taskId));
        Assert.Equal(FixedNowText, RawFirstStoredText(taskId));

        // The stored receipt still loads as the ORIGINAL one.
        var loaded = store.Load(taskId);
        Assert.NotNull(loaded);
        Assert.Equal(storedCanonical, CompletionReceiptCodec.Encode(loaded!.Receipt));
    }

    private static CompletionReceipt ConflictingCandidate(string kind, string taskId) => kind switch
    {
        "goal" => Receipt(goalId: "goal-other", taskId: taskId, metrics: Metrics(), gitStatus: Git()),
        "worker" => Receipt(workerId: "worker-other", taskId: taskId, metrics: Metrics(), gitStatus: Git()),
        // THE CASE-ONLY VECTORS: the two canonical texts differ ONLY by letter case, so an
        // ordinal-IGNORE-CASE comparison would falsely report AlreadyStored. A genuine conflict must
        // be decided ordinally.
        "case-only-goal" => Receipt(goalId: "GOAL-BASE", taskId: taskId, metrics: Metrics(), gitStatus: Git()),
        "case-only-output" => Receipt(taskId: taskId, output: "OUT-BASE", metrics: Metrics(), gitStatus: Git()),
        "iteration" => Receipt(taskId: taskId, iteration: 2, metrics: Metrics(), gitStatus: Git()),
        "occurrence" => Receipt(taskId: taskId, occurrence: 2, metrics: Metrics(), gitStatus: Git()),
        "attempt" => Receipt(taskId: taskId, attempt: 2, metrics: Metrics(), gitStatus: Git()),
        "phase-role" => Receipt(
            taskId: taskId, phase: GoalPhase.Review, role: WorkerRole.Reviewer, metrics: Metrics(), gitStatus: Git()),
        "status" => Receipt(taskId: taskId, status: TaskOutcome.Failed, metrics: Metrics(), gitStatus: Git()),
        "output" => Receipt(taskId: taskId, output: "out-other", metrics: Metrics(), gitStatus: Git()),
        "model" => Receipt(taskId: taskId, model: "model-other", metrics: Metrics(), gitStatus: Git()),
        "sha" => Receipt(taskId: taskId, sha: "sha-other", metrics: Metrics(), gitStatus: Git()),
        "metrics-values" => Receipt(taskId: taskId, metrics: Metrics(coverage: 12.5), gitStatus: Git()),
        "metrics-null" => Receipt(taskId: taskId, gitStatus: Git()),
        "git-values" => Receipt(taskId: taskId, metrics: Metrics(), gitStatus: Git(filesChanged: 9)),
        "git-null" => Receipt(taskId: taskId, metrics: Metrics()),
        _ => throw new InvalidOperationException($"Unknown conflict vector '{kind}'."),
    };

    /// <summary>
    /// THE EQUIVALENT NON-CANONICAL DUPLICATE: the row holds valid version-1 JSON for the SAME
    /// receipt, formatted differently (indented, and with a case-variant enum label). The zero-row
    /// insert is followed by a successful decode and an ORDINAL comparison of canonical texts, so the
    /// outcome is <see cref="CompletionReceiptWriteStatus.AlreadyStored"/> — and the stored bytes are
    /// left EXACTLY as seeded.
    /// </summary>
    [Fact]
    public void InsertOnce_EquivalentNonCanonicalStoredJson_AlreadyStored_RawBytesUntouched()
    {
        const string taskId = "task-noncanonical";
        var receipt = Receipt(taskId: taskId, metrics: Metrics(), gitStatus: Git());
        var canonical = CompletionReceiptCodec.Encode(receipt);

        // THE NON-CANONICAL BUT VALID EQUIVALENT: indentation + a case-variant enum VALUE label
        // (property names stay case-sensitive, values accept case variants and normalize on output).
        var nonCanonical = JsonSerializer
            .Serialize(JsonNode.Parse(canonical), new JsonSerializerOptions { WriteIndented = true })
            .Replace("\"coder\"", "\"CODER\"", StringComparison.Ordinal)
            .Replace("\"coding\"", "\"Coding\"", StringComparison.Ordinal);

        Assert.NotEqual(canonical, nonCanonical);
        // The equivalence is REAL: re-encoding the decode reproduces the canonical text.
        Assert.Equal(canonical, CompletionReceiptCodec.Encode(CompletionReceiptCodec.Decode(nonCanonical)));

        var seededTime = "2020-01-02T03:04:05.0000000Z";
        SeedReceiptRow(taskId, receipt.GoalId, nonCanonical, seededTime);

        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        var result = store.InsertOnce(receipt);

        Assert.Equal(CompletionReceiptWriteStatus.AlreadyStored, result.Status);
        Assert.Null(result.WriteException);
        // BYTE-FOR-BYTE UNCHANGED — the equivalent payload was NOT rewritten into canonical form.
        Assert.Equal(nonCanonical, RawPayload(taskId));
        Assert.Equal(seededTime, RawFirstStoredText(taskId));
        Assert.Equal(1L, ReceiptRowCount(taskId));

        // …and it still loads (the loaded receipt's canonical text equals the candidate's).
        var loaded = store.Load(taskId);
        Assert.NotNull(loaded);
        Assert.Equal(canonical, CompletionReceiptCodec.Encode(loaded!.Receipt));
        Assert.Equal(DateTime.ParseExact(seededTime, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind), loaded.FirstStoredAtUtc);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) Absence, and read/integrity errors that must THROW
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>ABSENCE IS THE ONLY NULL: a missing row loads as <c>null</c>, and nothing is written.</summary>
    [Fact]
    public void Load_MissingRow_ReturnsNull_AndWritesNothing()
    {
        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        Assert.Null(store.Load("task-absent"));
        Assert.Equal(0L, (long)RawScalar("SELECT COUNT(*) FROM completion_receipts")!);
    }

    /// <summary>BLANK IDENTITIES are refused before a context exists — no row, no statement.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Load_BlankTaskId_ThrowsArgumentException(string blank)
    {
        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        var ex = Assert.Throws<ArgumentException>(() => store.Load(blank));
        Assert.Equal("taskId", ex.ParamName);
        Assert.Equal(0, factory.CreateCount);
    }

    /// <summary>NULL IDENTITY is the null-argument refusal, also before a context exists.</summary>
    [Fact]
    public void Load_NullTaskId_ThrowsArgumentNull_AndAcquiresNothing()
    {
        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        var ex = Assert.Throws<ArgumentNullException>(() => store.Load(null!));
        Assert.Equal("taskId", ex.ParamName);
        Assert.Equal(0, factory.CreateCount);
    }

    /// <summary>
    /// CORRUPT / UNSUPPORTED PAYLOADS THROW on both paths — <c>Load</c> and the zero-row duplicate
    /// path — as read/integrity errors, never as <c>Conflict</c>/<c>AlreadyStored</c>/<c>Indeterminate</c>
    /// with a fabricated write exception.
    /// </summary>
    [Theory]
    [InlineData("malformed-json", "{\"version\":1,\"goalId\":")]
    [InlineData("not-json", "definitely not json")]
    [InlineData("json-null-root", "null")]
    [InlineData("array-root", "[]")]
    [InlineData("empty-string", "")]
    [InlineData("unsupported-version", "{\"version\":2}")]
    [InlineData("missing-member",
        "{\"version\":1,\"goalId\":\"g\",\"workerId\":\"w\",\"role\":\"coder\",\"slot\":{\"taskId\":\"t\",\"position\":{\"iteration\":1,\"phase\":\"coding\",\"occurrence\":1},\"attempt\":1},\"result\":{\"taskId\":\"t\",\"status\":\"completed\",\"output\":\"o\",\"model\":\"m\",\"iterationStartSha\":null,\"metrics\":null}}")]
    [InlineData("unknown-property",
        "{\"version\":1,\"extra\":true,\"goalId\":\"g\",\"workerId\":\"w\",\"role\":\"coder\",\"slot\":{\"taskId\":\"t\",\"position\":{\"iteration\":1,\"phase\":\"coding\",\"occurrence\":1},\"attempt\":1},\"result\":{\"taskId\":\"t\",\"status\":\"completed\",\"output\":\"o\",\"model\":\"m\",\"iterationStartSha\":null,\"metrics\":null,\"gitStatus\":null}}")]
    [InlineData("duplicate-property",
        "{\"version\":1,\"goalId\":\"g\",\"goalId\":\"g\",\"workerId\":\"w\",\"role\":\"coder\",\"slot\":{\"taskId\":\"t\",\"position\":{\"iteration\":1,\"phase\":\"coding\",\"occurrence\":1},\"attempt\":1},\"result\":{\"taskId\":\"t\",\"status\":\"completed\",\"output\":\"o\",\"model\":\"m\",\"iterationStartSha\":null,\"metrics\":null,\"gitStatus\":null}}")]
    [InlineData("numeric-enum",
        "{\"version\":1,\"goalId\":\"g\",\"workerId\":\"w\",\"role\":1,\"slot\":{\"taskId\":\"t\",\"position\":{\"iteration\":1,\"phase\":\"coding\",\"occurrence\":1},\"attempt\":1},\"result\":{\"taskId\":\"t\",\"status\":\"completed\",\"output\":\"o\",\"model\":\"m\",\"iterationStartSha\":null,\"metrics\":null,\"gitStatus\":null}}")]
    public void CorruptStoredPayload_LoadAndDuplicatePath_ThrowCodecError(string label, string payload)
    {
        var taskId = $"task-corrupt-{label}";
        SeedReceiptRow(taskId, "goal-corrupt", payload, FixedNowText);

        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        // LOAD THROWS.
        var loadError = Assert.Throws<CompletionReceiptCodecException>(() => store.Load(taskId));
        Assert.False(string.IsNullOrWhiteSpace(loadError.Message));

        // THE DUPLICATE PATH THROWS TOO — it is NOT a Conflict and NOT an Indeterminate.
        var candidate = Receipt(goalId: "goal-corrupt", taskId: taskId, metrics: Metrics(), gitStatus: Git());
        Assert.Throws<CompletionReceiptCodecException>(() => store.InsertOnce(candidate));

        // The corrupt row was left exactly as seeded — nothing repaired, nothing overwritten.
        Assert.Equal(payload, RawPayload(taskId));
        Assert.Equal(1L, ReceiptRowCount(taskId));
    }

    /// <summary>
    /// ROW/PAYLOAD IDENTITY MISMATCH: a decodable payload whose task id (or goal id) disagrees with
    /// the row is an integrity error and THROWS from both paths — an explicit
    /// <see cref="InvalidOperationException"/>, never a Conflict.
    /// </summary>
    [Theory]
    [InlineData("task")]
    [InlineData("goal")]
    public void RowPayloadIdentityMismatch_LoadAndDuplicatePath_ThrowInvalidOperation(string kind)
    {
        var taskId = $"task-mismatch-{kind}";
        var payloadTaskId = kind == "task" ? "task-in-payload" : taskId;
        var payloadGoalId = kind == "goal" ? "goal-in-payload" : "goal-mismatch";
        var payload = CompletionReceiptCodec.Encode(
            Receipt(goalId: payloadGoalId, taskId: payloadTaskId, metrics: Metrics(), gitStatus: Git()));

        SeedReceiptRow(taskId, "goal-mismatch", payload, FixedNowText);

        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        var loadError = Assert.Throws<InvalidOperationException>(() => store.Load(taskId));
        Assert.Contains("disagree", loadError.Message, StringComparison.Ordinal);

        var candidate = Receipt(goalId: "goal-mismatch", taskId: taskId, metrics: Metrics(), gitStatus: Git());
        var duplicateError = Assert.Throws<InvalidOperationException>(() => store.InsertOnce(candidate));
        Assert.Contains("disagree", duplicateError.Message, StringComparison.Ordinal);
        Assert.Equal(payload, RawPayload(taskId));
    }

    /// <summary>
    /// A VANISHED DUPLICATE ROW: a confirmed zero-row insert whose row is then not readable is an
    /// explicit <see cref="InvalidOperationException"/> — never a Conflict, never an AlreadyStored.
    /// The zero count is produced by genuinely SUPPRESSING the provider's execution at the EF
    /// interception point, so the statement reports zero rows AND no row exists: exactly the
    /// inconsistent state the guard exists for.
    /// </summary>
    [Fact]
    public void InsertOnce_ZeroRowsButRowMissing_ThrowsInvalidOperation_NotConflict()
    {
        const string taskId = "task-vanished";
        var receipt = Receipt(taskId: taskId, metrics: Metrics(), gitStatus: Git());
        var interceptor = new ReceiptInsertSuppressingInterceptor();
        var factory = NewFactory(interceptor);
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        var thrown = Assert.Throws<InvalidOperationException>(() => store.InsertOnce(receipt));

        Assert.Equal(1, interceptor.SuppressCount);
        Assert.Contains("missing after a zero-row insert", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(0L, ReceiptRowCount(taskId)); // the readback agrees: no row exists
    }

    /// <summary>
    /// AN UNMATERIALIZABLE TIMESTAMP is a read error and THROWS — it is never a defaulted instant and
    /// never a write outcome.
    /// </summary>
    [Fact]
    public void Load_UnmaterializableFirstStoredTimestamp_Throws()
    {
        const string taskId = "task-bad-time";
        var payload = CompletionReceiptCodec.Encode(Receipt(taskId: taskId, metrics: Metrics(), gitStatus: Git()));
        SeedReceiptRow(taskId, "goal-base", payload, "not-a-timestamp");

        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        var thrown = Record.Exception(() => store.Load(taskId));

        Assert.NotNull(thrown);
        // The failure is the timestamp materialization, NOT a payload refusal and NOT a write outcome.
        Assert.IsNotType<CompletionReceiptCodecException>(thrown);
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
        var receipt = Receipt(taskId: taskId, metrics: Metrics(), gitStatus: Git());
        SeedReceiptRow(taskId, receipt.GoalId, CompletionReceiptCodec.Encode(receipt), FixedNowText);

        var interceptor = new ReceiptSelectThrowingInterceptor();
        var factory = NewFactory(interceptor);
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        var loadThrown = Assert.Throws<InvalidOperationException>(() => store.Load(taskId));
        Assert.Same(interceptor.Sentinel, loadThrown);

        // The zero-row duplicate path issues its own SELECT and hits the same fault: it THROWS,
        // rather than returning Indeterminate with a fabricated WriteException.
        var insertThrown = Assert.Throws<InvalidOperationException>(() => store.InsertOnce(receipt));
        Assert.Same(interceptor.Sentinel, insertThrown);
        Assert.Equal(2, interceptor.ThrowCount);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (4) The one statement, pinned: parameterization, no pre-read, no transaction
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE WRITE SHAPE, PINNED: EXACTLY ONE command — the narrow parameterized
    /// <c>INSERT … ON CONFLICT(task_id) DO NOTHING</c> — with all four values bound as parameters and
    /// no literal value in the SQL, no SELECT/reader/scalar command, no UPDATE/DELETE and no explicit
    /// transaction begun.
    /// </summary>
    [Fact]
    public void InsertOnce_Stored_IssuesExactlyOneParameterizedInsert_NoPreReadNoTransaction()
    {
        const string taskId = "task-shape";
        var receipt = Receipt(goalId: "goal-shape", taskId: taskId, metrics: Metrics(), gitStatus: Git());
        var canonical = CompletionReceiptCodec.Encode(receipt);

        var capture = new ReceiptCommandCaptureInterceptor();
        var transactions = new ReceiptTransactionCountInterceptor();
        var factory = NewFactory(capture, transactions);
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        var result = store.InsertOnce(receipt);

        Assert.Equal(CompletionReceiptWriteStatus.Stored, result.Status);
        var command = Assert.Single(capture.Commands);
        Assert.Equal("NonQuery", command.Kind);
        Assert.StartsWith("INSERT INTO completion_receipts", command.Sql.TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ON CONFLICT(task_id) DO NOTHING", command.Sql, StringComparison.Ordinal);
        Assert.Equal(
            new[] { "@firstStoredAtUtc", "@goalId", "@payloadJson", "@taskId" },
            command.Parameters.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(taskId, command.Parameters["@taskId"]);
        Assert.Equal("goal-shape", command.Parameters["@goalId"]);
        Assert.Equal(canonical, command.Parameters["@payloadJson"]);
        Assert.Equal(FixedNowText, command.Parameters["@firstStoredAtUtc"]);

        // NO PRE-READ, and no other statement kind at all.
        Assert.DoesNotContain(capture.Commands, c => c.Kind is "Reader" or "Scalar");
        Assert.DoesNotContain(capture.Commands, c => c.Sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(capture.Commands, c => c.Sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(capture.Commands, c => c.Sql.Contains("DELETE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(capture.Commands, c => c.Sql.Contains("BEGIN", StringComparison.OrdinalIgnoreCase));

        // NO EXPLICIT TRANSACTION.
        Assert.Equal(0, transactions.StartCount);

        // THE SQL CARRIES NO LITERAL VALUES — every value travelled as a parameter.
        Assert.All(capture.Commands, c =>
        {
            Assert.DoesNotContain(taskId, c.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("goal-shape", c.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain(canonical, c.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain(FixedNowText, c.Sql, StringComparison.Ordinal);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (5) Write uncertainty — before and after the underlying autocommit
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A FAILURE BEFORE EXECUTION (<c>NonQueryExecuting</c>): the INSERT never ran, so the outcome is
    /// <see cref="CompletionReceiptWriteStatus.Indeterminate"/> carrying the EXACT sentinel, the
    /// fresh readback shows NO row, and an explicit retry settles to <c>Stored</c>.
    /// </summary>
    [Fact]
    public void InsertOnce_FailureBeforeExecution_IndeterminateExactException_AbsentRow_RetrySettlesStored()
    {
        const string taskId = "task-before-exec";
        var receipt = Receipt(taskId: taskId, metrics: Metrics(), gitStatus: Git());
        var interceptor = new ReceiptInsertThrowingInterceptor(ReceiptInsertFault.BeforeExecution);
        var factory = NewFactory(interceptor);
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        var result = store.InsertOnce(receipt);

        Assert.Equal(CompletionReceiptWriteStatus.Indeterminate, result.Status);
        Assert.Same(interceptor.Sentinel, result.WriteException);
        Assert.Equal(1, interceptor.FireCount);

        // THE READBACK DISTINGUISHES THE REALITY: the statement never ran, so the row is ABSENT.
        Assert.Equal(0L, ReceiptRowCount(taskId));

        // AN EXPLICIT RETRY SETTLES CORRECTLY.
        interceptor.Disarm();
        var retry = store.InsertOnce(receipt);
        Assert.Equal(CompletionReceiptWriteStatus.Stored, retry.Status);
        Assert.Null(retry.WriteException);
        Assert.Equal(1L, ReceiptRowCount(taskId));
        Assert.Equal(CompletionReceiptCodec.Encode(receipt), RawPayload(taskId));
    }

    /// <summary>
    /// A FAILURE AFTER THE UNDERLYING AUTOCOMMIT (<c>NonQueryExecuted</c>): the row IS durable even
    /// though the call threw, so the outcome is still
    /// <see cref="CompletionReceiptWriteStatus.Indeterminate"/> carrying the EXACT sentinel — and the
    /// fresh readback proves the row is PRESENT and complete. An explicit retry then settles to
    /// <c>AlreadyStored</c> against the very row the failed call wrote.
    /// </summary>
    [Fact]
    public void InsertOnce_FailureAfterAutocommit_IndeterminateExactException_PresentRow_RetrySettlesAlreadyStored()
    {
        const string taskId = "task-after-exec";
        var receipt = Receipt(taskId: taskId, metrics: Metrics(), gitStatus: Git());
        var canonical = CompletionReceiptCodec.Encode(receipt);
        var interceptor = new ReceiptInsertThrowingInterceptor(ReceiptInsertFault.AfterExecution);
        var factory = NewFactory(interceptor);
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        var result = store.InsertOnce(receipt);

        Assert.Equal(CompletionReceiptWriteStatus.Indeterminate, result.Status);
        Assert.Same(interceptor.Sentinel, result.WriteException);
        Assert.Equal(1, interceptor.FireCount);

        // THE READBACK DISTINGUISHES THE REALITY: the autocommit landed, so the row is PRESENT and
        // complete — the store made no claim about it, and this is the caller's evidence.
        Assert.Equal(1L, ReceiptRowCount(taskId));
        Assert.Equal(canonical, RawPayload(taskId));
        Assert.Equal(FixedNowText, RawFirstStoredText(taskId));

        // AN EXPLICIT RETRY SETTLES AGAINST THE ROW THE FAILED CALL WROTE.
        interceptor.Disarm();
        var retryStore = NewStore(factory, new FixedTimeProvider(FixedNow.AddMinutes(1)));
        var retry = retryStore.InsertOnce(receipt);
        Assert.Equal(CompletionReceiptWriteStatus.AlreadyStored, retry.Status);
        Assert.Equal(FixedNowText, RawFirstStoredText(taskId)); // the first-stored time did NOT move
    }

    /// <summary>
    /// AN UNEXPECTED AFFECTED-ROW COUNT IS NEVER A SUCCESS AND NEVER A DUPLICATE: <c>-1</c> (the
    /// provider's "unknown" count) and <c>2</c> both yield
    /// <see cref="CompletionReceiptWriteStatus.Indeterminate"/> carrying an
    /// <see cref="InvalidOperationException"/> that NAMES the count; the honest readback shows the
    /// autocommit's real effect, and an explicit retry settles to <c>AlreadyStored</c>.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void InsertOnce_UnexpectedAffectedRowCount_IndeterminateWithCountException_RetrySettles(int forcedCount)
    {
        var taskId = $"task-count-{forcedCount}";
        var receipt = Receipt(taskId: taskId, metrics: Metrics(), gitStatus: Git());
        var interceptor = new ReceiptRowCountInterceptor(forcedCount);
        var factory = NewFactory(interceptor);
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        var result = store.InsertOnce(receipt);

        Assert.Equal(CompletionReceiptWriteStatus.Indeterminate, result.Status);
        Assert.Equal(1, interceptor.OverrideCount);
        var error = Assert.IsType<InvalidOperationException>(result.WriteException);
        Assert.Contains(
            forcedCount.ToString(CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.Contains("expected exactly 0 or 1", error.Message, StringComparison.Ordinal);

        // The autocommit really inserted one row: the store claimed nothing, the readback tells the truth.
        Assert.Equal(1L, ReceiptRowCount(taskId));

        // An explicit retry settles against the real row.
        interceptor.Disarm();
        var retry = store.InsertOnce(receipt);
        Assert.Equal(CompletionReceiptWriteStatus.AlreadyStored, retry.Status);
        Assert.Equal(1L, ReceiptRowCount(taskId));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (6) Pre-write refusals — nothing acquired, nothing written
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>A NULL CANDIDATE is refused before a context exists.</summary>
    [Fact]
    public void InsertOnce_NullCandidate_ThrowsArgumentNull_AndAcquiresNothing()
    {
        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        var ex = Assert.Throws<ArgumentNullException>(() => store.InsertOnce(null!));
        Assert.Equal("candidate", ex.ParamName);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0L, (long)RawScalar("SELECT COUNT(*) FROM completion_receipts")!);
    }

    /// <summary>
    /// A CODEC REFUSAL is a pre-write refusal: an unrepresentable receipt (a null output, and an
    /// unpaired UTF-16 surrogate in the evidence text) THROWS with NO context acquired and NO
    /// statement issued.
    /// </summary>
    [Theory]
    [InlineData("null-output")]
    [InlineData("null-model")]
    [InlineData("null-metrics-summary")]
    [InlineData("unpaired-surrogate")]
    public void InsertOnce_UnrepresentableReceipt_ThrowsCodecError_AndAcquiresNothing(string kind)
    {
        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        var thrown = Assert.Throws<CompletionReceiptCodecException>(() => store.InsertOnce(Unrepresentable(kind)));

        Assert.False(string.IsNullOrWhiteSpace(thrown.Message));
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0L, (long)RawScalar("SELECT COUNT(*) FROM completion_receipts")!);
    }

    private static CompletionReceipt Unrepresentable(string kind)
    {
        const string taskId = "task-unrepresentable";
        return kind switch
        {
            "null-output" => Receipt(taskId: taskId, output: null!),
            "null-model" => Receipt(taskId: taskId, model: null!),
            "null-metrics-summary" => Receipt(
                taskId: taskId, metrics: new TaskMetrics { Summary = null! }),
            "unpaired-surrogate" => Receipt(taskId: taskId, output: "bad-\uD800-tail"),
            _ => throw new InvalidOperationException($"Unknown unrepresentable vector '{kind}'."),
        };
    }

    /// <summary>
    /// A CLOCK FAILURE propagates the EXACT exception and happens BEFORE any context is acquired or
    /// any statement is issued.
    /// </summary>
    [Fact]
    public void InsertOnce_ClockThrows_PropagatesExactException_AndAcquiresNothing()
    {
        var factory = NewFactory();
        var sentinel = new InvalidOperationException("clock sentinel");
        var store = NewStore(factory, new ThrowingTimeProvider(sentinel));

        var thrown = Assert.Throws<InvalidOperationException>(() => store.InsertOnce(Receipt(taskId: "task-clock")));

        Assert.Same(sentinel, thrown);
        Assert.Equal(0, factory.CreateCount);
    }

    /// <summary>
    /// A CONTEXT-ACQUISITION FAILURE propagates the EXACT exception with nothing to clean up: the
    /// disposal seam is never invoked because no context was ever acquired.
    /// </summary>
    [Fact]
    public void InsertOnce_ContextAcquisitionThrows_PropagatesExactException_AndDisposesNothing()
    {
        var sentinel = new InvalidOperationException("acquisition sentinel");
        var store = NewStore(new ReceiptThrowingContextFactory(sentinel), new FixedTimeProvider(FixedNow));
        var disposeCalls = 0;
        store.ContextDisposerForTest = _ =>
        {
            disposeCalls++;
            throw new InvalidOperationException("cleanup must not run without an acquisition");
        };

        var thrown = Assert.Throws<InvalidOperationException>(
            () => store.InsertOnce(Receipt(taskId: "task-acquire")));

        Assert.Same(sentinel, thrown);
        Assert.Equal(0, disposeCalls);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (7) The freeze — caller mutation at the factory boundary cannot change the payload
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FROZEN PAYLOAD: the candidate is encoded EXACTLY ONCE, before any database access, and the
    /// row identities come from decoding that text. A factory whose <c>CreateDbContext</c> mutates the
    /// caller's own evidence lists therefore CANNOT change what is stored — the stored payload equals
    /// the canonical text captured before the mutation, and the caller's lists really were mutated.
    /// </summary>
    [Fact]
    public void InsertOnce_CallerListMutatedAtFactoryBoundary_StoredPayloadIsTheFrozenCanonicalText()
    {
        var issues = new List<string> { "issue-1", "issue-2" };
        var changedFiles = new List<string> { "src/a.cs", "src/b.cs" };
        var receipt = Receipt(
            taskId: "task-freeze",
            metrics: Metrics(issues: issues),
            gitStatus: Git(changedFiles: changedFiles));

        var canonical = CompletionReceiptCodec.Encode(receipt);

        var factory = NewFactory();
        // THE BOUNDARY MUTATION: it runs INSIDE CreateDbContext, i.e. after the encode and before the
        // statement — the exact window a re-encoding implementation would be vulnerable in.
        factory.OnCreate = () =>
        {
            issues.Clear();
            issues.Add("mutated");
            changedFiles.Clear();
            changedFiles.Add("mutated.cs");
        };
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));

        var result = store.InsertOnce(receipt);

        Assert.Equal(CompletionReceiptWriteStatus.Stored, result.Status);
        // The hook really ran (the mutation is not vacuous).
        Assert.Equal(["mutated"], issues);
        Assert.Equal(["mutated.cs"], changedFiles);
        // …and the stored payload is the FROZEN text, not a re-encoding of the mutated graph.
        Assert.Equal(canonical, RawPayload("task-freeze"));
        Assert.Contains("\"issues\":[\"issue-1\",\"issue-2\"]", RawPayload("task-freeze")!, StringComparison.Ordinal);
        Assert.Contains("\"changedFiles\":[\"src/a.cs\",\"src/b.cs\"]", RawPayload("task-freeze")!, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (8) The guarded cleanup — never masked
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A FAILING CONTEXT DISPOSAL PRESERVES THE CONFIRMED OUTCOMES: <c>Stored</c>,
    /// <c>AlreadyStored</c> and <c>Conflict</c> are all returned unchanged (with a
    /// <c>context-dispose</c> warning logged), and the disposal failure's own sentinel never escapes.
    /// The injected disposer really disposes the context first, so no resource leaks.
    /// </summary>
    [Theory]
    [InlineData("stored")]
    [InlineData("already-stored")]
    [InlineData("conflict")]
    public void InsertOnce_DisposalFails_ConfirmedOutcomesPreserved(string scenario)
    {
        var taskId = $"task-dispose-{scenario}";
        var stored = Receipt(taskId: taskId, metrics: Metrics(), gitStatus: Git());
        if (scenario != "stored")
            SeedReceiptRow(taskId, stored.GoalId, CompletionReceiptCodec.Encode(stored), FixedNowText);

        var logger = new TestLogger<CompletionReceiptStore>();
        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow), logger);
        var sentinel = new InvalidOperationException("dispose sentinel");
        var disposeCalls = 0;
        store.ContextDisposerForTest = context =>
        {
            disposeCalls++;
            context.Dispose(); // the REAL release still happens
            throw sentinel;
        };

        var candidate = scenario == "conflict"
            ? Receipt(taskId: taskId, output: "out-other", metrics: Metrics(), gitStatus: Git())
            : stored;

        var result = store.InsertOnce(candidate);

        var expected = scenario switch
        {
            "stored" => CompletionReceiptWriteStatus.Stored,
            "already-stored" => CompletionReceiptWriteStatus.AlreadyStored,
            _ => CompletionReceiptWriteStatus.Conflict,
        };
        Assert.Equal(expected, result.Status);
        Assert.Null(result.WriteException);
        Assert.Equal(1, disposeCalls);
        // The guarded warning carries the identifiers and the disposal failure's own message — and
        // the failure NEVER escapes as an exception (the outcome above is what the caller sees).
        var warning = Assert.Single(logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
        Assert.Contains("context-dispose", warning.Message, StringComparison.Ordinal);
        Assert.Contains(taskId, warning.Message, StringComparison.Ordinal);
        Assert.Contains("dispose sentinel", warning.Message, StringComparison.Ordinal);
        Assert.Null(warning.Exception);
    }

    /// <summary>
    /// A FAILING DISPOSAL PRESERVES WRITE UNCERTAINTY TOO: the <c>Indeterminate</c> outcome and its
    /// EXACT <see cref="CompletionReceiptWriteResult.WriteException"/> survive a throwing disposer.
    /// </summary>
    [Fact]
    public void InsertOnce_DisposalFails_IndeterminateEvidencePreserved()
    {
        const string taskId = "task-dispose-indeterminate";
        var receipt = Receipt(taskId: taskId, metrics: Metrics(), gitStatus: Git());
        var insertFault = new ReceiptInsertThrowingInterceptor(ReceiptInsertFault.BeforeExecution);
        var logger = new TestLogger<CompletionReceiptStore>();
        var factory = NewFactory(insertFault);
        var store = NewStore(factory, new FixedTimeProvider(FixedNow), logger);
        store.ContextDisposerForTest = context =>
        {
            context.Dispose();
            throw new InvalidOperationException("dispose sentinel");
        };

        var result = store.InsertOnce(receipt);

        Assert.Equal(CompletionReceiptWriteStatus.Indeterminate, result.Status);
        Assert.Same(insertFault.Sentinel, result.WriteException);
        Assert.Contains(logger.LogEntries, e => e.LogLevel == LogLevel.Warning
            && e.Message.Contains("context-dispose", StringComparison.Ordinal));
    }

    /// <summary>
    /// A FAILING DISPOSAL PRESERVES A PROPAGATING READ ERROR: the codec exception still escapes (by
    /// identity) with the disposal warning logged — the cleanup never replaces it.
    /// </summary>
    [Fact]
    public void InsertOnce_DisposalFails_PropagatingReadErrorPreserved()
    {
        const string taskId = "task-dispose-read-error";
        SeedReceiptRow(taskId, "goal-base", "not json at all", FixedNowText);

        var logger = new TestLogger<CompletionReceiptStore>();
        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow), logger);
        store.ContextDisposerForTest = context =>
        {
            context.Dispose();
            throw new InvalidOperationException("dispose sentinel");
        };

        var thrown = Assert.Throws<CompletionReceiptCodecException>(
            () => store.Load(taskId));

        Assert.False(string.IsNullOrWhiteSpace(thrown.Message));
        Assert.Contains(logger.LogEntries, e => e.LogLevel == LogLevel.Warning
            && e.Message.Contains("context-dispose", StringComparison.Ordinal));
    }

    /// <summary>
    /// A THROWING LOGGER CANNOT MASK ANYTHING: with the logger armed to throw on every write, a
    /// disposal failure still leaves the confirmed outcome intact, the uncertainty evidence intact,
    /// and the propagating read error intact.
    /// </summary>
    [Fact]
    public void InsertOnce_ThrowingLogger_NeverMasksOutcomeOrException()
    {
        const string taskId = "task-throwing-logger";
        var receipt = Receipt(taskId: taskId, metrics: Metrics(), gitStatus: Git());

        // (a) A CONFIRMED OUTCOME with a failing disposal and a throwing logger.
        var loggerA = new ThrowingLogger<CompletionReceiptStore>();
        var factoryA = NewFactory();
        var storeA = NewStore(factoryA, new FixedTimeProvider(FixedNow), loggerA);
        loggerA.Arm();
        storeA.ContextDisposerForTest = context =>
        {
            context.Dispose();
            throw new InvalidOperationException("dispose sentinel");
        };

        var stored = storeA.InsertOnce(receipt);
        Assert.Equal(CompletionReceiptWriteStatus.Stored, stored.Status);
        Assert.Null(stored.WriteException);
        Assert.True(loggerA.ThrowCount >= 1, "the guarded warning never reached the throwing logger");

        // (b) WRITE UNCERTAINTY with a throwing logger: the EXACT evidence still comes back.
        var loggerB = new ThrowingLogger<CompletionReceiptStore>();
        var insertFault = new ReceiptInsertThrowingInterceptor(ReceiptInsertFault.BeforeExecution);
        var factoryB = NewFactory(insertFault);
        var storeB = NewStore(factoryB, new FixedTimeProvider(FixedNow), loggerB);
        loggerB.Arm();

        var uncertain = storeB.InsertOnce(Receipt(taskId: "task-throwing-logger-uncertain"));
        Assert.Equal(CompletionReceiptWriteStatus.Indeterminate, uncertain.Status);
        Assert.Same(insertFault.Sentinel, uncertain.WriteException);
        Assert.True(loggerB.ThrowCount >= 1);

        // (c) A PROPAGATING READ ERROR with a throwing logger.
        const string readTaskId = "task-throwing-logger-read";
        SeedReceiptRow(readTaskId, "goal-base", "not json at all", FixedNowText);
        var loggerC = new ThrowingLogger<CompletionReceiptStore>();
        var factoryC = NewFactory();
        var storeC = NewStore(factoryC, new FixedTimeProvider(FixedNow), loggerC);
        loggerC.Arm();

        Assert.Throws<CompletionReceiptCodecException>(() => storeC.Load(readTaskId));
    }

    /// <summary>
    /// A CLEANUP EXCEPTION WHOSE <c>Message</c> GETTER ITSELF THROWS cannot escape the finally: the
    /// whole diagnostic (message access included) sits inside the no-throw guard, so the confirmed
    /// outcome stays authoritative.
    /// </summary>
    [Fact]
    public void InsertOnce_DisposalThrowsUnreadableMessage_ConfirmedOutcomeStillReturned()
    {
        const string taskId = "task-unreadable-cleanup";
        var receipt = Receipt(taskId: taskId, metrics: Metrics(), gitStatus: Git());
        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));
        store.ContextDisposerForTest = context =>
        {
            context.Dispose();
            throw new ThrowingMessageException("the disposal failure is unreadable");
        };

        var result = store.InsertOnce(receipt);

        Assert.Equal(CompletionReceiptWriteStatus.Stored, result.Status);
        Assert.Null(result.WriteException);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (9) The two-connection race — the database primary key arbitrates
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// TWO INDEPENDENT FACTORIES/CONNECTIONS, GATED AT THE INSERT: both producers reach the statement
    /// boundary before either executes, so the race is real and NOT ordered by sleeping. Identical
    /// candidates settle as exactly one <c>Stored</c> plus one <c>AlreadyStored</c>.
    /// </summary>
    [Fact]
    public void ConcurrentIdenticalInserts_AcrossTwoConnections_ProduceStoredAndAlreadyStored()
    {
        const string taskId = "task-race-identical";
        var receipt = Receipt(taskId: taskId, metrics: Metrics(), gitStatus: Git());
        var canonical = CompletionReceiptCodec.Encode(receipt);

        using var gate = new ReceiptInsertGateInterceptor(participants: 2);
        var factoryA = NewFactory(gate);
        var factoryB = NewFactory(gate);
        var storeA = NewStore(factoryA, new FixedTimeProvider(FixedNow));
        var storeB = NewStore(factoryB, new FixedTimeProvider(FixedNow.AddSeconds(1)));

        var results = RunRace(
            () => storeA.InsertOnce(receipt),
            () => storeB.InsertOnce(receipt));

        Assert.Equal(2, gate.ArrivalCount);
        Assert.Equal(1, results.Count(r => r.Status == CompletionReceiptWriteStatus.Stored));
        Assert.Equal(1, results.Count(r => r.Status == CompletionReceiptWriteStatus.AlreadyStored));
        Assert.All(results, r => Assert.Null(r.WriteException));

        // EXACTLY ONE COMPLETE WINNER ROW.
        Assert.Equal(1L, ReceiptRowCount(taskId));
        Assert.Equal(canonical, RawPayload(taskId));
        var storedText = RawFirstStoredText(taskId);
        Assert.Equal(1, new[] { FixedNowText, FixedNow.AddSeconds(1).UtcDateTime.ToString("O", CultureInfo.InvariantCulture) }
            .Count(candidate => candidate == storedText));
    }

    /// <summary>
    /// THE DIFFERING-CANDIDATE RACE: two independent connections gated at the INSERT, with candidates
    /// that share the task id but differ in every other respect. The outcome is exactly one
    /// <c>Stored</c> plus one <c>Conflict</c>, and the surviving row is ONE COMPLETE winner — its
    /// payload is the winner's canonical text and its first-stored time is the winner's clock.
    /// </summary>
    [Fact]
    public void ConcurrentDifferingInserts_AcrossTwoConnections_ProduceStoredAndConflict_WithOneCompleteWinner()
    {
        const string taskId = "task-race-differing";
        var candidateA = Receipt(
            goalId: "goal-race-a", workerId: "worker-race-a", taskId: taskId,
            output: "out-race-a", metrics: Metrics(summary: "summary-race-a"), gitStatus: Git());
        var candidateB = Receipt(
            goalId: "goal-race-b", workerId: "worker-race-b", taskId: taskId,
            output: "out-race-b", metrics: Metrics(summary: "summary-race-b"), gitStatus: Git());
        var canonicalA = CompletionReceiptCodec.Encode(candidateA);
        var canonicalB = CompletionReceiptCodec.Encode(candidateB);
        Assert.NotEqual(canonicalA, canonicalB);

        using var gate = new ReceiptInsertGateInterceptor(participants: 2);
        var factoryA = NewFactory(gate);
        var factoryB = NewFactory(gate);
        var storeA = NewStore(factoryA, new FixedTimeProvider(FixedNow));
        var storeB = NewStore(factoryB, new FixedTimeProvider(FixedNow.AddSeconds(1)));

        var results = RunRace(
            () => storeA.InsertOnce(candidateA),
            () => storeB.InsertOnce(candidateB));

        Assert.Equal(2, gate.ArrivalCount);
        Assert.Equal(1, results.Count(r => r.Status == CompletionReceiptWriteStatus.Stored));
        Assert.Equal(1, results.Count(r => r.Status == CompletionReceiptWriteStatus.Conflict));
        Assert.All(results, r => Assert.Null(r.WriteException));

        // EXACTLY ONE COMPLETE WINNER: the row matches whichever producer actually inserted.
        Assert.Equal(1L, ReceiptRowCount(taskId));
        var payload = RawPayload(taskId);
        var storedText = RawFirstStoredText(taskId);
        if (results[0].Status == CompletionReceiptWriteStatus.Stored)
        {
            Assert.Equal(canonicalA, payload);
            Assert.Equal(FixedNowText, storedText);
        }
        else
        {
            Assert.Equal(canonicalB, payload);
            Assert.Equal(FixedNow.AddSeconds(1).UtcDateTime.ToString("O", CultureInfo.InvariantCulture), storedText);
        }

        // The loser's candidate was NOT written anywhere: only the winner's goal is on the row.
        Assert.Equal(payload == canonicalA ? "goal-race-a" : "goal-race-b",
            RawScalar($"SELECT goal_id FROM completion_receipts WHERE task_id = '{taskId}'"));
    }

    /// <summary>
    /// Runs two producers on DEDICATED threads that both start from the same rendezvous, and joins
    /// both with a BOUNDED wait. All tasks are drained in <c>finally</c>, so a hang is a failure
    /// rather than a leak. No sleep-based ordering is used anywhere.
    /// </summary>
    private static List<CompletionReceiptWriteResult> RunRace(
        Func<CompletionReceiptWriteResult> first,
        Func<CompletionReceiptWriteResult> second)
    {
        var results = new CompletionReceiptWriteResult?[2];
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
                Name = $"receipt-race-{index}",
            };
        }

        try
        {
            foreach (var thread in threads)
                thread.Start();

            foreach (var thread in threads)
                Assert.True(thread.Join(RaceTimeout), "A completion-receipt race producer never finished.");

            foreach (var failure in failures)
                Assert.Null(failure);

            return [.. results.Select(r => r ?? throw new InvalidOperationException("A producer produced no result."))];
        }
        finally
        {
            // DRAIN: every thread is bounded-joined, and any that somehow outlived the join is
            // abandoned as a background thread only after the assertions above have run.
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
    // (10) Isolation — receipt operations touch ONLY completion_receipts
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE BLAST RADIUS IS ONE TABLE: a seeded pipeline row, task-mapping row and conversation row are
    /// byte-identical before and after a full receipt workload (a stored insert, a load, an equivalent
    /// duplicate and a conflict), and no extra rows appear anywhere.
    /// </summary>
    [Fact]
    public void ReceiptOperations_LeaveSeededPipelineMappingAndConversationRowsUnchanged()
    {
        ExecuteRaw(
            """
            INSERT INTO pipelines (goal_id, description, goal_json, phase, metrics_json, active_task_id, created_at)
            VALUES ('goal-seeded', 'Seeded', '{"id":"goal-seeded","description":"seeded","repositories":["r"]}',
                    'Coding', '{}', 'task-seeded', '2026-01-01T00:00:00.0000000Z')
            """);
        ExecuteRaw("INSERT INTO task_mappings (task_id, goal_id) VALUES ('task-seeded', 'goal-seeded')");
        ExecuteRaw(
            "INSERT INTO conversation_entries (goal_id, seq, role, content) VALUES ('goal-seeded', 0, 'user', 'hello')");

        var pipelineBefore = ReadWholeRow("pipelines", "goal_id", "goal-seeded");
        var mappingBefore = ReadWholeRow("task_mappings", "task_id", "task-seeded");
        var conversationBefore = ReadWholeRow("conversation_entries", "goal_id", "goal-seeded");

        var factory = NewFactory();
        var store = NewStore(factory, new FixedTimeProvider(FixedNow));
        var receipt = Receipt(taskId: "task-seeded-receipt", metrics: Metrics(), gitStatus: Git());

        Assert.Equal(CompletionReceiptWriteStatus.Stored, store.InsertOnce(receipt).Status);
        Assert.NotNull(store.Load("task-seeded-receipt"));
        Assert.Equal(CompletionReceiptWriteStatus.AlreadyStored, store.InsertOnce(receipt).Status);
        Assert.Equal(
            CompletionReceiptWriteStatus.Conflict,
            store.InsertOnce(Receipt(taskId: "task-seeded-receipt", output: "other", metrics: Metrics())).Status);

        Assert.Equal(pipelineBefore, ReadWholeRow("pipelines", "goal_id", "goal-seeded"));
        Assert.Equal(mappingBefore, ReadWholeRow("task_mappings", "task_id", "task-seeded"));
        Assert.Equal(conversationBefore, ReadWholeRow("conversation_entries", "goal_id", "goal-seeded"));
        Assert.Equal(1L, (long)RawScalar("SELECT COUNT(*) FROM pipelines")!);
        Assert.Equal(1L, (long)RawScalar("SELECT COUNT(*) FROM task_mappings")!);
        Assert.Equal(1L, (long)RawScalar("SELECT COUNT(*) FROM conversation_entries")!);
    }

    // ───────────────────────────── fixture types ─────────────────────────────

    /// <summary>A clock frozen at one instant, so the stored timestamp text is predictable.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A clock that always throws the pre-created sentinel.</summary>
    private sealed class ThrowingTimeProvider(Exception sentinel) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw sentinel;
    }

    /// <summary>
    /// A factory handing out store-OWNED contexts, each on its own connection to the file-backed
    /// database. The contexts own those connections (they were created from a connection string), so
    /// disposing the context really releases the file handle. <see cref="OnCreate"/> is the BOUNDARY
    /// HOOK the freeze vector mutates the caller's graph from.
    /// </summary>
    private sealed class TestContextFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly string _connectionString;
        private readonly IInterceptor[] _interceptors;
        private readonly List<CopilotHiveDbContext> _contexts = [];
        private int _createCount;

        public TestContextFactory(string connectionString, IInterceptor[] interceptors)
        {
            _connectionString = connectionString;
            _interceptors = interceptors;
        }

        /// <summary>Runs INSIDE <see cref="CreateDbContext"/>, after the store's single encode.</summary>
        public Action? OnCreate { get; set; }

        public int CreateCount => Volatile.Read(ref _createCount);

        public CopilotHiveDbContext CreateDbContext()
        {
            Interlocked.Increment(ref _createCount);
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

    /// <summary>A factory whose acquisition always throws the pre-created sentinel.</summary>
    private sealed class ReceiptThrowingContextFactory(Exception sentinel) : IDbContextFactory<CopilotHiveDbContext>
    {
        public CopilotHiveDbContext CreateDbContext() => throw sentinel;
    }
}

/// <summary>A captured command attempt: its kind, its SQL text and its bound parameters.</summary>
internal sealed record ReceiptCapturedCommand(
    string Kind,
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters);

/// <summary>
/// Captures every command attempt issued through a context — the parameterization and
/// no-pre-read/no-transaction evidence.
/// </summary>
internal sealed class ReceiptCommandCaptureInterceptor : DbCommandInterceptor
{
    private readonly List<ReceiptCapturedCommand> _commands = [];

    public IReadOnlyList<ReceiptCapturedCommand> Commands
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
            _commands.Add(new ReceiptCapturedCommand(kind, command.CommandText, parameters));
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command, "NonQuery");
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command, "NonQuery");
        return ValueTask.FromResult(result);
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

/// <summary>Counts every transaction begin the context attempts — the no-explicit-transaction proof.</summary>
internal sealed class ReceiptTransactionCountInterceptor : DbTransactionInterceptor
{
    private int _startCount;

    public int StartCount => Volatile.Read(ref _startCount);

    /// <inheritdoc />
    public override InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result)
    {
        Interlocked.Increment(ref _startCount);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _startCount);
        return ValueTask.FromResult(result);
    }
}

/// <summary>Where the injected insert fault fires.</summary>
internal enum ReceiptInsertFault
{
    /// <summary>Before the provider executes the statement — nothing is written.</summary>
    BeforeExecution,

    /// <summary>After the provider executed the statement — the autocommit has already landed.</summary>
    AfterExecution,
}

/// <summary>
/// Throws a pre-created sentinel at the completion-receipt INSERT, either BEFORE the provider
/// executes it (<c>NonQueryExecuting</c>) or AFTER the underlying autocommit
/// (<c>NonQueryExecuted</c>). The sentinel is a single instance exposed for identity assertions, and
/// <see cref="FireCount"/> proves the injection really fired. <see cref="Disarm"/> lets a test prove
/// that an explicit retry settles.
/// </summary>
internal sealed class ReceiptInsertThrowingInterceptor : DbCommandInterceptor
{
    private readonly ReceiptInsertFault _fault;
    private int _armed = 1;
    private int _fireCount;

    public ReceiptInsertThrowingInterceptor(ReceiptInsertFault fault) => _fault = fault;

    /// <summary>The pre-created instance every armed fault throws.</summary>
    public InvalidOperationException Sentinel { get; } = new("completion-receipt insert SENTINEL");

    /// <summary>How many times the sentinel was thrown (the injection really fired).</summary>
    public int FireCount => Volatile.Read(ref _fireCount);

    /// <summary>Stops the injection so a subsequent call can settle.</summary>
    public void Disarm() => Volatile.Write(ref _armed, 0);

    private bool IsReceiptInsert(DbCommand command) =>
        command.CommandText.TrimStart().StartsWith("INSERT INTO completion_receipts", StringComparison.OrdinalIgnoreCase);

    private void ThrowIfTargeted(DbCommand command)
    {
        if (Volatile.Read(ref _armed) == 0 || !IsReceiptInsert(command))
            return;

        Interlocked.Increment(ref _fireCount);
        throw Sentinel;
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        if (_fault == ReceiptInsertFault.BeforeExecution)
            ThrowIfTargeted(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (_fault == ReceiptInsertFault.BeforeExecution)
            ThrowIfTargeted(command);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        if (_fault == ReceiptInsertFault.AfterExecution)
            ThrowIfTargeted(command);
        return result;
    }
}

/// <summary>
/// Substitutes the provider's post-execution affected-row count for the completion-receipt INSERT —
/// the unexpected-count (never a success) and vanished-duplicate-row vectors.
/// <see cref="OverrideCount"/> proves the substitution really happened.
/// </summary>
internal sealed class ReceiptRowCountInterceptor : DbCommandInterceptor
{
    private readonly int _forcedCount;
    private int _armed = 1;
    private int _overrideCount;

    public ReceiptRowCountInterceptor(int forcedCount) => _forcedCount = forcedCount;

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
                "INSERT INTO completion_receipts", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        Interlocked.Increment(ref _overrideCount);
        return _forcedCount;
    }
}

/// <summary>
/// SUPPRESSES the provider's execution of the completion-receipt INSERT and reports a genuine
/// zero-row result — the vanished-duplicate-row vector. Unlike a count override this really prevents
/// the write, so the zero count and the absent row AGREE, which is the inconsistent state the store's
/// missing-row guard exists for. <see cref="SuppressCount"/> proves the suppression really happened.
/// </summary>
internal sealed class ReceiptInsertSuppressingInterceptor : DbCommandInterceptor
{
    private int _suppressCount;

    /// <summary>How many times the INSERT execution was suppressed.</summary>
    public int SuppressCount => Volatile.Read(ref _suppressCount);

    private bool IsReceiptInsert(DbCommand command) =>
        command.CommandText.TrimStart().StartsWith("INSERT INTO completion_receipts", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        if (!IsReceiptInsert(command))
            return result;

        Interlocked.Increment(ref _suppressCount);
        return InterceptionResult<int>.SuppressWithResult(0);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (!IsReceiptInsert(command))
            return ValueTask.FromResult(result);

        Interlocked.Increment(ref _suppressCount);
        return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
    }
}

/// <summary>
/// Throws a pre-created sentinel whenever the completion-receipt SELECT is about to execute — the
/// read-fault vectors (a read failure must remain a THROW, never write uncertainty).
/// </summary>
internal sealed class ReceiptSelectThrowingInterceptor : DbCommandInterceptor
{
    private int _throwCount;

    /// <summary>The pre-created instance every read fault throws.</summary>
    public InvalidOperationException Sentinel { get; } = new("completion-receipt read SENTINEL");

    /// <summary>How many times the sentinel was thrown (the injection really fired).</summary>
    public int ThrowCount => Volatile.Read(ref _throwCount);

    private void ThrowIfTargeted(DbCommand command)
    {
        var trimmed = command.CommandText.TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || !command.CommandText.Contains("completion_receipts", StringComparison.OrdinalIgnoreCase))
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

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTargeted(command);
        return ValueTask.FromResult(result);
    }
}

/// <summary>
/// The RACE GATE: every producer that reaches the completion-receipt INSERT is parked at a
/// <see cref="Barrier"/> until all participants have arrived, so the race is genuinely concurrent and
/// ordered by the rendezvous — never by sleeping. A rendezvous that times out throws, so a
/// regression fails loudly instead of hanging.
/// </summary>
internal sealed class ReceiptInsertGateInterceptor : DbCommandInterceptor, IDisposable
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(60);

    private readonly Barrier _barrier;
    private int _arrivalCount;

    public ReceiptInsertGateInterceptor(int participants) => _barrier = new Barrier(participants);

    /// <summary>How many producers reached the gate.</summary>
    public int ArrivalCount => Volatile.Read(ref _arrivalCount);

    private void Gate(DbCommand command)
    {
        if (!command.CommandText.TrimStart().StartsWith(
                "INSERT INTO completion_receipts", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Interlocked.Increment(ref _arrivalCount);
        if (!_barrier.SignalAndWait(GateTimeout))
            throw new InvalidOperationException("The completion-receipt insert gate never rendezvoused.");
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Gate(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Gate(command);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public void Dispose() => _barrier.Dispose();
}
