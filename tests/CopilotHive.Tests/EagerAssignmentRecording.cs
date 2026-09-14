using CopilotHive.Persistence;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// THE EAGER-RECORDING WIRING for an existing fixture that hands the REAL
/// <see cref="GrpcWorkerGateway"/> a real publisher: a real
/// <see cref="WorkerAssignmentContextStore"/> over a private in-memory SQLite database, plus the real
/// <see cref="WorkerAssignmentPublisher"/> over it. <see cref="Start"/> returns the anchor that keeps
/// the in-memory database alive for the test's lifetime.
/// </summary>
/// <remarks>
/// A SMALL SHARED HELPER, NOT A SECOND HARNESS: three existing fixtures need the same wiring, and
/// there is no transport, no stream reader/writer and no teardown ledger here. The anchor connection
/// must stay open for the duration of the test — an in-memory SQLite database is destroyed when its
/// last connection closes — so the returned <see cref="Session"/> is what the caller disposes.
/// </remarks>
internal static class EagerAssignmentRecording
{
    /// <summary>
    /// Opens the anchor connection, creates the schema with the existing shared test factory, and
    /// returns the REAL store and the REAL publisher the gateway must delegate to.
    /// </summary>
    /// <param name="manager">The routing authority holding the delivered task's pipeline.</param>
    /// <param name="pool">The pool the delivered worker instance is pinned to.</param>
    /// <returns>The session to dispose once the test is done.</returns>
    public static Session Start(GoalPipelineManager manager, WorkerPool pool)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite(connection)
            .Options;

        var store = new WorkerAssignmentContextStore(
            new SharedDbContextFactory(connection, options),
            NullLogger<WorkerAssignmentContextStore>.Instance);

        return new Session(connection, store, new WorkerAssignmentPublisher(manager, pool, store));
    }

    /// <summary>The live anchor connection, the real store and the real publisher.</summary>
    internal sealed class Session(
        SqliteConnection connection,
        WorkerAssignmentContextStore store,
        WorkerAssignmentPublisher publisher) : IDisposable
    {
        /// <summary>The REAL publisher the gateway under test is constructed with.</summary>
        public WorkerAssignmentPublisher Publisher { get; } = publisher;

        /// <summary>
        /// The REAL store, used as TEST OBSERVATION only — the production path never reads back.
        /// </summary>
        public WorkerAssignmentContextStore Store { get; } = store;

        /// <inheritdoc />
        public void Dispose() => connection.Dispose();
    }
}

/// <summary>
/// THE PUBLISHER OBSERVATION SPY: a minimal <see cref="IWorkerAssignmentPublisher"/> that records the
/// EXACT arguments the caller forwarded and makes the caller's COMPLETION deterministically
/// observable.
/// </summary>
/// <remarks>
/// <para>
/// IT OBSERVES, IT DOES NOT VIOLATE. Unlike the Ready suite's ungated control (which publishes BEFORE
/// recording, deliberately breaking the row-before-publication ordering), this spy performs NO
/// recording and NO channel write at all: it is handed to <see cref="GrpcWorkerGateway"/> so a
/// fixture can prove which worker INSTANCE, which <see cref="WorkTask"/> INSTANCE and which
/// <see cref="CancellationToken"/> the gateway forwarded, how many times, and that the gateway's own
/// task remains INCOMPLETE until the publisher returns.
/// </para>
/// <para>
/// IT WRITES TO THE CHANNEL ONLY IF ASKED. <see cref="PublishToChannel"/> defaults to <c>false</c>,
/// so the spy's default behavior touches nothing observable. The two signals are PRE-CREATED inside
/// the constructor (with <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>) so a waiter
/// installed by the fixture can never miss them, and waiting on them is bounded by the fixture.
/// </para>
/// </remarks>
internal sealed class GatewayPublishSpy : IWorkerAssignmentPublisher
{
    private readonly TaskCompletionSource _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _invocationCount;

    /// <summary>How many times <see cref="PublishAsync"/> was invoked.</summary>
    public int InvocationCount => Volatile.Read(ref _invocationCount);

    /// <summary>The exact worker instance the caller forwarded, or <c>null</c> before any invocation.</summary>
    public ConnectedWorker? ReceivedWorker { get; private set; }

    /// <summary>The exact <see cref="WorkTask"/> instance the caller forwarded, or <c>null</c> before any invocation.</summary>
    public WorkTask? ReceivedTask { get; private set; }

    /// <summary>The exact token the caller forwarded.</summary>
    public CancellationToken ReceivedCancellationToken { get; private set; }

    /// <summary>When <c>true</c>, the spy performs the caller's own channel write for the delivered task.</summary>
    public bool PublishToChannel { get; init; }

    /// <summary>Completes when <see cref="PublishAsync"/> has been entered, before it returns.</summary>
    public Task Entered => _entered.Task;

    /// <summary>The token the spy's own invocation is parked on until the fixture releases it.</summary>
    public Task ReleaseToken => _release.Task;

    /// <summary>Releases a parked invocation so <see cref="PublishAsync"/> can return.</summary>
    public void Release() => _release.TrySetResult();

    /// <inheritdoc />
    public async Task PublishAsync(ConnectedWorker worker, WorkTask task, CancellationToken cancellationToken)
    {
        ReceivedWorker = worker;
        ReceivedTask = task;
        ReceivedCancellationToken = cancellationToken;
        Interlocked.Increment(ref _invocationCount);

        // THE ENTRY SIGNAL IS RAISED BEFORE THE PARK: a fixture waiting on it learns of the
        // invocation, and can then drive the caller's own task to its completion point.
        _entered.TrySetResult();

        if (PublishToChannel)
        {
            await worker.MessageChannel.Writer.WriteAsync(
                new CopilotHive.Shared.Grpc.OrchestratorMessage
                {
                    Assignment = GrpcMapper.ToGrpc(task),
                },
                cancellationToken);
        }

        // THE COMPLETION IS THE FIXTURE'S TO GRANT: the awaiting gateway task stays incomplete until
        // Release() is called.
        await _release.Task;
    }
}

/// <summary>
/// THE GATEWAY LOGGER DOUBLE: records every entry as (level, message, arguments) and can be armed to
/// THROW for a specific level or for any write at all.
/// </summary>
/// <remarks>
/// MODELLED ON THE EXISTING IN-REPO DOUBLE (<c>CapturingLogger&lt;T&gt;</c> of the composer attachment
/// suite), extended with the throwing arm because the gateway's guarded warning is only observable
/// through a logger that is asked to fail. <see cref="Emitted"/> preserves the RAW argument array, so
/// a fixture can assert the structured fields (goal, task, worker, reason) rather than parsing the
/// formatted text.
/// </remarks>
internal sealed class CapturingGatewayLogger : ILogger<GrpcWorkerGateway>
{
    private readonly Lock _gate = new();

    /// <summary>Every emitted entry, in emission order.</summary>
    public List<(LogLevel Level, string Message, IReadOnlyList<object?> Arguments)> Emitted { get; } = [];

    /// <summary>When set, a write at exactly this level throws <see cref="WriteException"/>.</summary>
    public LogLevel? ThrowOnLevel { get; init; }

    /// <summary>When <c>true</c>, EVERY write throws <see cref="WriteException"/>.</summary>
    public bool ThrowOnAnyWrite { get; init; }

    /// <summary>How many writes actually threw — the proof the throwing arm was reached.</summary>
    public int ThrowCount { get; private set; }

    /// <summary>The exception thrown by a write, when throwing is armed.</summary>
    public Exception WriteException { get; } = new InvalidOperationException("gateway-logger-sentinel");

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var arguments = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
            ? pairs.Select(pair => pair.Value).ToArray()
            : [];

        lock (_gate)
            Emitted.Add((logLevel, message, arguments));

        if (ThrowOnAnyWrite || (ThrowOnLevel is LogLevel armed && armed == logLevel))
        {
            ThrowCount++;
            throw WriteException;
        }
    }
}

/// <summary>
/// A KNOWN, DISTINGUISHABLE POST-RECORD CHANNEL FAULT: a publisher that RECORDS the delivered
/// context through the REAL <see cref="WorkerAssignmentContextStore"/> (so the row really exists when
/// the fault fires) and then throws
/// <see cref="PostRecordChannelFaultException"/> — a type minted by the FIXTURE, so a dispatch-level
/// vector can assert the EXACT original exception identity end-to-end instead of merely "something
/// threw that is not a recording failure".
/// </summary>
/// <remarks>
/// <para>
/// THE FAULT IS RAISED WHERE THE PUBLISHER'S OWN CHANNEL WRITE RAISES: after the one insert, with no
/// recording failure of any kind. That is precisely the boundary the dispatch's ambiguity-PRESERVE
/// covers, and it is reachable through the real <see cref="GrpcWorkerGateway"/> because the fault is
/// not a <see cref="WorkerAssignmentRecordingException"/> and so is never converted to
/// <see cref="WorkerTaskSendOutcome.Blocked"/>.
/// </para>
/// <para>
/// THE CONTEXT IS CONSTRUCTED FROM THE DELIVERED TASK'S OWN OWNERSHIP, using the same existing
/// <see cref="GoalPipeline.CaptureAdmissionOwnership"/> surface the real publisher reads, so the
/// recorded row carries the DELIVERED task's real goal, worker and slot attempt. It performs NO
/// validation beyond the store's own — the recording path is not re-implemented here, it is exercised
/// through the existing store.
/// </para>
/// </remarks>
internal sealed class PostRecordChannelFaultPublisher(
    GoalPipelineManager manager, WorkerAssignmentContextStore store) : IWorkerAssignmentPublisher
{
    /// <summary>The exact exception a caller observes when this publisher's post-record step fails.</summary>
    public PostRecordChannelFaultException Fault { get; } = new("post-record channel fault");

    /// <summary>How many recorded rows this publisher committed.</summary>
    public int RecordCount { get; private set; }

    /// <inheritdoc />
    public Task PublishAsync(ConnectedWorker worker, WorkTask task, CancellationToken cancellationToken)
    {
        // The pipeline the delivered task belongs to is resolved by the DELIVERED task id — the same
        // routing authority the real publisher consults — and only the delivered slot's identity is
        // read from it, so no registry rule is re-implemented here.
        var pipeline = manager.GetByTaskId(task.TaskId)
            ?? throw new InvalidOperationException(
                $"no pipeline is registered for the delivered task '{task.TaskId}'");

        var slot = pipeline.GetSlotsForTest()
            .Select(view => view.Slot)
            .First(candidate => candidate is not null
                && string.Equals(candidate.TaskId, task.TaskId, StringComparison.Ordinal));

        var context = new WorkerAssignmentContext(
            pipeline.GoalId, worker.Id, task.Role, slot, task.Model);

        var recorded = store.InsertOnce(context);
        if (recorded.Status is not (WorkerAssignmentWriteStatus.Recorded
            or WorkerAssignmentWriteStatus.AlreadyRecorded))
        {
            throw new InvalidOperationException(
                $"the fault publisher could not record the delivered task ({recorded.Status})");
        }

        RecordCount++;

        // THE POST-RECORD FAULT — the exact point the real publisher's channel write would run. It is
        // NOT a recording failure, so the gateway propagates it instead of reporting Blocked.
        throw Fault;
    }
}

/// <summary>
/// THE FIXTURE-MINTED POST-RECORD FAULT TYPE: distinguishable BY TYPE from every production failure,
/// so a vector can assert the exact original exception identity that leaves the dispatch.
/// </summary>
internal sealed class PostRecordChannelFaultException(string message) : Exception(message);
